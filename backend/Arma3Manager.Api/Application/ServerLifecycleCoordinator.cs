using System.Diagnostics;
using Arma3Manager.Api.Configuration;
using Arma3Manager.Api.Contracts;
using Arma3Manager.Api.Domain;
using Arma3Manager.Api.Infrastructure.Persistence;

namespace Arma3Manager.Api.Application;

public sealed record LifecycleReservation(bool Accepted, string? OperationId, ServerLifecycleStatus Status);
public sealed record LifecycleCommandResult(bool Accepted, ServerLifecycleStatus Status, string? Error = null, string? Code = null);

/// <summary>
/// Serializes every main-server lifecycle command, persists its authoritative state, and reconciles it with
/// the operating system. RuntimeState remains the low-level process owner; callers must enter through here.
/// </summary>
public sealed class ServerLifecycleCoordinator : BackgroundService
{
    readonly ILogger<ServerLifecycleCoordinator> logger;
    readonly AppConfig config;
    readonly ServerPaths paths;
    readonly SqliteStore store;
    readonly RuntimeState runtime;
    readonly string instanceId = Guid.NewGuid().ToString("N");
    readonly SemaphoreSlim commandGate = new(1, 1);
    readonly object startGate = new();
    CancellationTokenSource? startCancellation;
    Task? startTask;
    string? activeStartOperation;

    public ServerLifecycleCoordinator(ILogger<ServerLifecycleCoordinator> logger, AppConfig config, ServerPaths paths, SqliteStore store, RuntimeState runtime)
    {
        this.logger = logger;
        this.config = config;
        this.paths = paths;
        this.store = store;
        this.runtime = runtime;
        runtime.RunEnded += OnRunEnded;
    }

    public async Task InitializeAsync() => await ReconcileAsync(initializing: true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await commandGate.WaitAsync(stoppingToken);
                try { await ReconcileAsync(initializing: false); }
                finally { commandGate.Release(); }
            }
            catch (Exception exception) { logger.LogWarning(exception, "Unable to reconcile the Arma server lifecycle"); }
        }
    }

    public async Task<ServerLifecycleStatus> GetStatusAsync() => ToStatus(await store.GetServerRuntimeAsync());

    public async Task<LifecycleReservation> TryBeginStartAsync()
    {
        await commandGate.WaitAsync();
        try
        {
            await ReconcileAsync(initializing: false);
            var operationId = Guid.NewGuid().ToString("N");
            var reserved = await store.TryReserveServerStartAsync(operationId, instanceId, DateTimeOffset.UtcNow);
            if (!reserved.Accepted) return new(false, null, ToStatus(reserved.State));
            lock (startGate)
            {
                startCancellation?.Dispose();
                startCancellation = new CancellationTokenSource();
                activeStartOperation = operationId;
                startTask = null;
            }
            return new(true, operationId, ToStatus(reserved.State));
        }
        finally { commandGate.Release(); }
    }

    public void QueueStart(string operationId, Func<CancellationToken, Task> workflow)
    {
        CancellationToken token;
        lock (startGate)
        {
            if (activeStartOperation != operationId || startCancellation is null)
                throw new InvalidOperationException("The server start reservation is no longer active");
            token = startCancellation.Token;
            startTask = Task.Run(async () =>
            {
                try { await workflow(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    runtime.Push("stderr", $"Server start failed: {exception.Message}");
                    await MarkFaultedAsync(operationId, exception.Message);
                }
                finally
                {
                    lock (startGate)
                    {
                        if (activeStartOperation == operationId)
                        {
                            activeStartOperation = null;
                            startCancellation?.Dispose();
                            startCancellation = null;
                        }
                    }
                }
            });
        }
    }

    public async Task<bool> SetStartStageAsync(string operationId, string phase, string stage)
    {
        var current = await store.GetServerRuntimeAsync();
        if (current.OperationId != operationId || current.Phase is not ("preparing" or "starting")) return false;
        var now = DateTimeOffset.UtcNow;
        return await store.UpdateServerRuntimeAsync(current with { Phase = phase, Stage = stage, UpdatedAt = now }, operationId);
    }

    public async Task<ServerProcessIdentity> LaunchAsync(
        string operationId,
        string binary,
        IEnumerable<string> arguments,
        string workingDirectory,
        string? profilesDirectory)
    {
        if (!await SetStartStageAsync(operationId, "starting", "launching"))
            throw new OperationCanceledException("The start reservation was cancelled");

        var lockFile = Path.Combine(config.Arma3Dir, ".manager", "main-server.lock");
        var identity = runtime.Start(binary, arguments, workingDirectory, profilesDirectory, lockFile);
        var runId = runtime.RunId ?? throw new InvalidOperationException("The launched server has no run identifier");
        try
        {
            await Task.Delay(200);
            if (!runtime.IsRunning)
            {
                await store.CloseOpenServerSessionsAsync("start_failed");
                throw new InvalidOperationException("The server process exited during launch; another process may hold the singleton lock");
            }
            await store.CloseOpenServerSessionsAsync("superseded", runId);
            await store.StartServerSessionAsync(new(runId, DateTimeOffset.UtcNow, identity.Pid));

            var current = await store.GetServerRuntimeAsync();
            if (current.Phase != "starting")
                throw new OperationCanceledException("The server start was cancelled while the process launched");
            var now = DateTimeOffset.UtcNow;
            var running = current with
            {
                Phase = "running",
                Stage = null,
                RunId = runId,
                Pid = identity.Pid,
                ProcessStartedAtUtcTicks = identity.StartedAtUtcTicks,
                BinaryPath = identity.BinaryPath,
                Since = now,
                UpdatedAt = now,
                LastError = null,
                ConflictingPids = []
            };
            if (!await store.UpdateServerRuntimeAsync(running, operationId))
                throw new OperationCanceledException("The server start was cancelled before launch completed");
            return identity;
        }
        catch
        {
            if (runtime.IsRunning) await runtime.StopAsync("cancelled");
            await store.CloseOpenServerSessionsAsync("start_failed");
            throw;
        }
    }

    public void StartHeadlessClient(string file, IEnumerable<string> arguments, string workingDirectory, int index) =>
        runtime.StartHeadlessClient(file, arguments, workingDirectory, index);

    public async Task<LifecycleCommandResult> StopAsync(string reason = "stopped")
    {
        await commandGate.WaitAsync();
        try
        {
            var current = await store.GetServerRuntimeAsync();
            if (current.Phase == "blocked")
                return new(false, ToStatus(current), "An unmanaged Arma server process is active", "unmanaged_server_conflict");
            if (current.Phase is "stopped" or "faulted")
                return new(false, ToStatus(current), "Server is not running", "server_not_running");

            var now = DateTimeOffset.UtcNow;
            await store.UpdateServerRuntimeAsync(current with { Phase = "stopping", Stage = "cancelling", Since = now, UpdatedAt = now }, current.OperationId);
            Task? pending;
            lock (startGate)
            {
                startCancellation?.Cancel();
                pending = startTask;
            }
            if (pending is not null)
            {
                try { await pending.WaitAsync(TimeSpan.FromMinutes(10)); }
                catch (TimeoutException) { return new(false, await GetStatusAsync(), "Timed out waiting for server preparation to cancel", "stop_timeout"); }
            }

            var runId = runtime.RunId ?? current.RunId;
            if (runtime.IsRunning) await runtime.StopAsync(reason);
            if (runId is not null) await store.EndServerSessionAsync(new(runId, DateTimeOffset.UtcNow, null, reason));
            var stoppedAt = DateTimeOffset.UtcNow;
            var stopped = new PersistedServerRuntime("stopped", null, null, null, null, null, null, null, stoppedAt, stoppedAt, null, []);
            await store.UpdateServerRuntimeAsync(stopped);
            return new(true, ToStatus(stopped));
        }
        finally { commandGate.Release(); }
    }

    public async Task<LifecycleReservation> TryBeginRestartAsync()
    {
        await commandGate.WaitAsync();
        try
        {
            await ReconcileAsync(initializing: false);
            var current = await store.GetServerRuntimeAsync();
            if (current.Phase != "running" || !current.Managed())
                return new(false, null, ToStatus(current));

            var now = DateTimeOffset.UtcNow;
            await store.UpdateServerRuntimeAsync(current with { Phase = "stopping", Stage = "restarting", Since = now, UpdatedAt = now }, current.OperationId);
            var runId = runtime.RunId ?? current.RunId;
            if (runtime.IsRunning) await runtime.StopAsync("restarted");
            if (runId is not null) await store.EndServerSessionAsync(new(runId, DateTimeOffset.UtcNow, null, "restarted"));

            var operationId = Guid.NewGuid().ToString("N");
            var preparingAt = DateTimeOffset.UtcNow;
            var preparing = new PersistedServerRuntime("preparing", "validating", operationId, instanceId, null, null, null, null, preparingAt, preparingAt, null, []);
            await store.UpdateServerRuntimeAsync(preparing);
            lock (startGate)
            {
                startCancellation?.Dispose();
                startCancellation = new CancellationTokenSource();
                activeStartOperation = operationId;
                startTask = null;
            }
            return new(true, operationId, ToStatus(preparing));
        }
        finally { commandGate.Release(); }
    }

    async Task MarkFaultedAsync(string operationId, string error)
    {
        var current = await store.GetServerRuntimeAsync();
        if (current.OperationId != operationId || current.Phase is "stopping" or "running" || runtime.IsRunning) return;
        var now = DateTimeOffset.UtcNow;
        await store.UpdateServerRuntimeAsync(current with
        {
            Phase = "faulted", Stage = null, RunId = null, Pid = null, ProcessStartedAtUtcTicks = null,
            BinaryPath = null, Since = now, UpdatedAt = now, LastError = error
        }, operationId);
    }

    async void OnRunEnded(ServerRunEnded ended)
    {
        try
        {
            await store.EndServerSessionAsync(ended);
            var current = await store.GetServerRuntimeAsync();
            if (current.RunId != ended.RunId) return;
            var now = DateTimeOffset.UtcNow;
            var expectedStop = ended.Reason is "stopped" or "restarted" or "cancelled";
            var next = current with
            {
                Phase = expectedStop ? "stopped" : "faulted",
                Stage = null,
                OperationId = null,
                OwnerInstanceId = null,
                RunId = null,
                Pid = null,
                ProcessStartedAtUtcTicks = null,
                BinaryPath = null,
                Since = now,
                UpdatedAt = now,
                LastError = expectedStop ? null : $"Server exited unexpectedly ({ended.Reason})"
            };
            await store.UpdateServerRuntimeAsync(next, current.OperationId);
        }
        catch (Exception exception) { logger.LogError(exception, "Unable to persist server exit for run {RunId}", ended.RunId); }
    }

    async Task ReconcileAsync(bool initializing)
    {
        var current = await store.GetServerRuntimeAsync();
        var operationIsLocal = false;
        lock (startGate) operationIsLocal = activeStartOperation is not null && activeStartOperation == current.OperationId;
        if (!initializing && operationIsLocal)
        {
            await store.UpdateServerRuntimeAsync(current with { UpdatedAt = DateTimeOffset.UtcNow }, current.OperationId);
            return;
        }
        if (!operationIsLocal && current.Phase is ("preparing" or "starting") && current.OwnerInstanceId is not null &&
            DateTimeOffset.UtcNow - current.UpdatedAt < TimeSpan.FromSeconds(10))
            return;

        if (current.Pid is { } pid && current.RunId is { } runId && current.ProcessStartedAtUtcTicks is { } ticks && current.BinaryPath is { } binary &&
            IsMatchingProcess(pid, ticks, binary))
        {
            if (!runtime.IsRunning) runtime.Adopt(pid, runId, binary);
            if (current.Phase != "running")
            {
                var now = DateTimeOffset.UtcNow;
                await store.UpdateServerRuntimeAsync(current with { Phase = "running", Stage = null, Since = now, UpdatedAt = now, LastError = null });
            }
            await store.CloseOpenServerSessionsAsync("superseded", runId);
            return;
        }

        await store.CloseOpenServerSessionsAsync("interrupted");
        var conflicts = FindUnmanagedMainServerPids(current.Pid).ToArray();
        var changedAt = DateTimeOffset.UtcNow;
        if (conflicts.Length > 0)
        {
            var blocked = new PersistedServerRuntime("blocked", "external_process", null, null, null, null, null, null,
                changedAt, changedAt, "An unmanaged Arma server process is active", conflicts);
            await store.UpdateServerRuntimeAsync(blocked);
            return;
        }

        if (current.Phase is not ("stopped" or "faulted") || current.Pid is not null)
        {
            var error = current.Phase is "preparing" or "starting"
                ? "Server start was interrupted before the process became healthy"
                : current.Phase == "blocked" ? null : current.LastError;
            var nextPhase = error is null ? "stopped" : "faulted";
            var reconciled = new PersistedServerRuntime(nextPhase, null, null, null, null, null, null, null,
                changedAt, changedAt, error, []);
            await store.UpdateServerRuntimeAsync(reconciled);
        }
    }

    bool IsMatchingProcess(int pid, long startedAtUtcTicks, string binaryPath)
    {
        try
        {
            using var candidate = Process.GetProcessById(pid);
            if (candidate.HasExited || candidate.StartTime.ToUniversalTime().Ticks != startedAtUtcTicks) return false;
            var actual = ProcessBinary(candidate);
            return actual is not null && Path.GetFullPath(actual).Equals(Path.GetFullPath(binaryPath), StringComparison.Ordinal);
        }
        catch { return false; }
    }

    IEnumerable<int> FindUnmanagedMainServerPids(int? managedPid)
    {
        if (!OperatingSystem.IsLinux()) yield break;
        var expected = new[] { paths.Arma3Bin, Path.Combine(config.Arma3Dir, "arma3server_x64"), Path.Combine(config.Arma3Dir, "arma3server") }
            .Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in Process.GetProcesses())
        {
            using (candidate)
            {
                if (candidate.Id == managedPid || candidate.Id == Environment.ProcessId) continue;
                string? binary;
                try { binary = ProcessBinary(candidate); } catch { continue; }
                if (binary is null || !expected.Contains(Path.GetFullPath(binary))) continue;
                var commandLine = ReadCommandLine(candidate.Id);
                if (commandLine.Any(argument => argument.Equals("-client", StringComparison.OrdinalIgnoreCase))) continue;
                yield return candidate.Id;
            }
        }
    }

    static string? ProcessBinary(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            var target = File.ResolveLinkTarget($"/proc/{process.Id}/exe", returnFinalTarget: true);
            return target?.FullName;
        }
        return process.MainModule?.FileName;
    }

    static string[] ReadCommandLine(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries); }
        catch { return []; }
    }

    static ServerLifecycleStatus ToStatus(PersistedServerRuntime state) => new(
        state.Phase,
        state.Phase == "running",
        state.Phase is "preparing" or "starting" or "stopping",
        state.Stage,
        state.OperationId,
        state.Since,
        state.LastError,
        state.Managed(),
        state.Pid,
        state.RunId,
        state.ConflictingPids);

    public override void Dispose()
    {
        runtime.RunEnded -= OnRunEnded;
        startCancellation?.Dispose();
        commandGate.Dispose();
        base.Dispose();
    }
}

static class PersistedServerRuntimeExtensions
{
    public static bool Managed(this PersistedServerRuntime state) => state.Pid is not null && state.RunId is not null && state.ConflictingPids.Length == 0;
}
