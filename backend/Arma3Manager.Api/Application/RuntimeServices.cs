using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using Arma3Manager.Api.Contracts;
using Arma3Manager.Api.Domain;
using Arma3Manager.Api.Infrastructure;

namespace Arma3Manager.Api.Application;

public sealed record TaskRunResult(int ExitCode, IReadOnlyList<string> Output);

public sealed record LogReadResult(
    IReadOnlyList<LogEntry> Entries,
    bool HasGap,
    long RequestedId,
    long? OldestAvailableId,
    long? NewestAvailableId);

/// <summary>Thread-safe bounded history and notification source for the live log stream.</summary>
public sealed class LogHub
{
    public const int DefaultCapacity = 5_000;
    public const int MaxLineBytes = 64 * 1024;
    const string TruncationSuffix = " … [truncated at 64 KiB]";

    readonly LogEntry?[] entries;
    readonly object gate = new();
    int start;
    int count;
    long nextLogId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
    TaskCompletionSource<bool> logSignal = NewLogSignal();
    public event Action<LogEntry>? EntryPushed;

    public LogHub() : this(DefaultCapacity) { }

    public LogHub(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        entries = new LogEntry[capacity];
    }

    public int Capacity => entries.Length;

    public IReadOnlyList<LogEntry> Snapshot(int? limit = null)
    {
        lock (gate)
        {
            var take = Math.Min(count, Math.Max(0, limit ?? count));
            var result = new LogEntry[take];
            var skip = count - take;
            for (var index = 0; index < take; index++)
                result[index] = entries[(start + skip + index) % entries.Length]!;
            return result;
        }
    }

    public LogReadResult ReadAfter(long id)
    {
        lock (gate) return ReadAfterLocked(id);
    }

    public async Task<LogReadResult> WaitForAfterAsync(long id, TimeSpan timeout, CancellationToken ct)
    {
        Task signal;
        lock (gate)
        {
            var pending = ReadAfterLocked(id);
            if (pending.Entries.Count > 0 || pending.HasGap) return pending;
            signal = logSignal.Task;
        }

        try { await signal.WaitAsync(timeout, ct); }
        catch (TimeoutException) { }

        lock (gate) return ReadAfterLocked(id);
    }

    static TaskCompletionSource<bool> NewLogSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LogEntry Push(string type, string data, string source = "manager", string? runId = null)
    {
        TaskCompletionSource<bool> signal;
        LogEntry entry;
        lock (gate)
        {
            entry = new(type, Truncate(data ?? ""), DateTimeOffset.UtcNow, ++nextLogId, source, runId);
            if (count < entries.Length)
            {
                entries[(start + count) % entries.Length] = entry;
                count++;
            }
            else
            {
                entries[start] = entry;
                start = (start + 1) % entries.Length;
            }
            signal = logSignal;
            logSignal = NewLogSignal();
        }
        signal.TrySetResult(true);
        EntryPushed?.Invoke(entry);
        return entry;
    }

    LogReadResult ReadAfterLocked(long id)
    {
        if (count == 0) return new([], false, id, null, null);

        var oldest = entries[start]!.Id;
        var newest = entries[(start + count - 1) % entries.Length]!.Id;
        var hasGap = id > 0 && (id < oldest - 1 || id > newest);
        var effectiveId = hasGap ? oldest - 1 : id;
        var pending = new List<LogEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var entry = entries[(start + index) % entries.Length]!;
            if (entry.Id > effectiveId) pending.Add(entry);
        }
        return new(pending, hasGap, id, oldest, newest);
    }

    static string Truncate(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= MaxLineBytes) return value;
        var budget = MaxLineBytes - Encoding.UTF8.GetByteCount(TruncationSuffix);
        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, middle)) <= budget) low = middle;
            else high = middle - 1;
        }
        if (low < value.Length && low > 0 && char.IsHighSurrogate(value[low - 1]) && char.IsLowSurrogate(value[low])) low--;
        return value[..low] + TruncationSuffix;
    }
}

/// <summary>Owns the Arma process and serialized maintenance tasks.</summary>
public sealed class RuntimeState(LogHub logHub)
{
    Process? process;
    string? runId;
    readonly Dictionary<string, string> requestedEndReasons = new(StringComparer.Ordinal);
    readonly HashSet<string> endedRuns = new(StringComparer.Ordinal);
    readonly List<Process> headlessClients = [];
    CancellationTokenSource? rptCts;
    readonly SemaphoreSlim taskGate = new(1, 1);
    readonly HashSet<string> queuedTaskKeys = new(StringComparer.Ordinal);

    public RuntimeState() : this(new LogHub()) { }
    public IReadOnlyList<LogEntry> Logs => logHub.Snapshot();
    public IReadOnlyList<LogEntry> GetLogs(int limit) => logHub.Snapshot(limit);
    public string? RunId => runId;
    public event Action<ServerRunStarted>? RunStarted;
    public event Action<ServerRunEnded>? RunEnded;

    public Task<IReadOnlyList<LogEntry>> WaitForLogsAfterAsync(long id, TimeSpan timeout, CancellationToken ct) =>
        WaitForEntriesAsync(id, timeout, ct);

    async Task<IReadOnlyList<LogEntry>> WaitForEntriesAsync(long id, TimeSpan timeout, CancellationToken ct) =>
        (await logHub.WaitForAfterAsync(id, timeout, ct)).Entries;

    public LogReadResult ReadLogsAfter(long id) => logHub.ReadAfter(id);
    public Task<LogReadResult> WaitForLogReadAsync(long id, TimeSpan timeout, CancellationToken ct) =>
        logHub.WaitForAfterAsync(id, timeout, ct);

    public bool IsRunning => process is { HasExited: false };
    public bool IsMaintenanceBusy
    {
        get { lock (queuedTaskKeys) return taskGate.CurrentCount == 0 || queuedTaskKeys.Count > 0; }
    }
    public int? ProcessId => IsRunning ? process!.Id : null;
    public int[] HeadlessClientPids
    {
        get { lock (headlessClients) return headlessClients.Where(hc => hc is { HasExited: false }).Select(hc => hc.Id).ToArray(); }
    }

    public void Start(string file, IEnumerable<string> arguments, string workingDirectory, string? profilesDir = null)
    {
        var startedAtUtc = DateTime.UtcNow;
        var currentRunId = Guid.NewGuid().ToString("N");
        runId = currentRunId;
        var started = StartProcess(file, arguments, workingDirectory, source: "arma", runId: currentRunId);
        process = started;
        RunStarted?.Invoke(new(currentRunId, DateTimeOffset.UtcNow, started.Id));
        Push("system", $"Started {file} PID {started.Id}", "manager", currentRunId);
        started.Exited += (_, _) =>
        {
            Push("system", $"Server exited with code {started.ExitCode}", "manager", currentRunId);
            string reason;
            lock (requestedEndReasons)
            {
                reason = requestedEndReasons.Remove(currentRunId, out var requested)
                    ? requested
                    : started.ExitCode == 0 ? "process_exit" : "crash";
            }
            lock (endedRuns)
            {
                if (endedRuns.Add(currentRunId))
                    RunEnded?.Invoke(new(currentRunId, DateTimeOffset.UtcNow, started.ExitCode, reason));
            }
            lock (headlessClients)
            {
                foreach (var hc in headlessClients) if (hc is { HasExited: false }) hc.Kill(entireProcessTree: true);
                headlessClients.Clear();
            }
        };

        rptCts?.Cancel();
        if (!string.IsNullOrWhiteSpace(profilesDir))
        {
            var cts = new CancellationTokenSource();
            rptCts = cts;
            _ = TailRptAsync(profilesDir, startedAtUtc, currentRunId, cts.Token);
        }
    }

    // Arma writes most connection/desync/addon-mismatch diagnostics to its .rpt report file in the profiles
    // directory, not to stdout — so the console log alone misses kicks, BE actions, and network errors that
    // explain "sometimes I can't connect" symptoms. This polls for the newest .rpt created after server start
    // and streams new lines into the same log feed the panel already displays.
    async Task TailRptAsync(string profilesDir, DateTime startedAtUtc, string runIdForRpt, CancellationToken ct)
    {
        string? currentFile = null;
        long position = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (currentFile is null && Directory.Exists(profilesDir))
                {
                    var newest = Directory.EnumerateFiles(profilesDir, "*.rpt")
                        .Select(path => new FileInfo(path))
                        .Where(info => info.LastWriteTimeUtc >= startedAtUtc.AddSeconds(-5))
                        .OrderByDescending(info => info.LastWriteTimeUtc)
                        .FirstOrDefault();
                    if (newest is not null)
                    {
                        currentFile = newest.FullName;
                        position = 0;
                        Push("system", $"Tailing RPT log: {newest.Name}", "manager", runIdForRpt);
                    }
                }
                if (currentFile is not null && File.Exists(currentFile))
                {
                    await using var stream = new FileStream(currentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (stream.Length < position) position = 0; // rotated/truncated
                    stream.Seek(position, SeekOrigin.Begin);
                    using var reader = new StreamReader(stream);
                    string? line;
                    while ((line = await reader.ReadLineAsync(ct)) is not null)
                        Push("rpt", line, "arma", runIdForRpt);
                    position = stream.Position;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Transient IO error while Arma is mid-write; retry on the next tick.
            }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    // Headless clients are additional instances of the same server binary launched in -client mode, connecting
    // back to the main server over loopback to take over AI processing. They're started after the main server
    // so they have something to connect to, and are tracked separately so Stop() always takes all of them down
    // together — an orphaned HC process would otherwise keep running (and keep its slot on the server) after
    // the panel reports the server as stopped.
    public void StartHeadlessClient(string file, IEnumerable<string> arguments, string workingDirectory, int index)
    {
        var hc = StartProcess(file, arguments, workingDirectory, source: "arma", runId: runId);
        lock (headlessClients) headlessClients.Add(hc);
        Push("system", $"Started headless client {index} PID {hc.Id}", "manager", runId);
        hc.Exited += (_, _) =>
        {
            lock (headlessClients) headlessClients.Remove(hc);
            Push("system", $"Headless client {index} exited with code {hc.ExitCode}", "manager", runId);
        };
    }

    public void Stop(string reason = "stopped")
    {
        if (runId is not null)
            lock (requestedEndReasons) requestedEndReasons[runId] = reason;
        if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        lock (headlessClients)
        {
            foreach (var hc in headlessClients) if (hc is { HasExited: false }) hc.Kill(entireProcessTree: true);
            headlessClients.Clear();
        }
        Push("system", "Server stopped", "manager", runId);
        rptCts?.Cancel();
    }

    public async Task StopAsync(string reason = "stopped", CancellationToken ct = default)
    {
        var stopping = process;
        Stop(reason);
        if (stopping is null || stopping.HasExited) return;
        try { await stopping.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct); }
        catch (TimeoutException) { Push("stderr", "Timed out waiting for the server process to stop", "manager", runId); }
    }

    public bool RunTask(string file, IEnumerable<string> arguments, string kind, Func<int, Task>? onExit = null, string? dedupeKey = null)
    {
        var key = dedupeKey ?? kind;
        lock (queuedTaskKeys)
        {
            if (!queuedTaskKeys.Add(key))
            {
                Push("system", $"Task {kind} ignored because {key} is already queued or running");
                return false;
            }
        }
        var captured = arguments.ToArray();
        _ = Task.Run(async () =>
        {
            try { await RunTaskAsync(file, captured, kind, onExit); }
            finally { lock (queuedTaskKeys) queuedTaskKeys.Remove(key); }
        });
        return true;
    }

    public async Task<int> RunTaskAsync(string file, IEnumerable<string> arguments, string kind, Func<int, Task>? onExit = null)
        => (await RunTaskCaptureAsync(file, arguments, kind, onExit)).ExitCode;

    public async Task<TaskRunResult> RunTaskCaptureAsync(string file, IEnumerable<string> arguments, string kind, Func<int, Task>? onExit = null)
    {
        var captured = arguments.ToArray();
        var output = new ConcurrentQueue<string>();
        await taskGate.WaitAsync();
        try
        {
            using var task = StartProcess(file, captured, Directory.GetCurrentDirectory(), output.Enqueue, "steamcmd");
            Push("system", $"Task {kind} started PID {task.Id}", "steamcmd");
            Push("system", $"Command: {CommandLog.Format(file, RedactSteamArgs(captured))}", "steamcmd");
            await task.WaitForExitAsync();
            Push("system", $"Task {kind} exited with code {task.ExitCode}", "steamcmd");
            if (onExit is not null) await onExit(task.ExitCode);
            return new(task.ExitCode, output.ToArray());
        }
        catch (Exception exception)
        {
            Push("stderr", $"Task {kind} failed: {exception.Message}", "steamcmd");
            return new(-1, output.ToArray());
        }
        finally { taskGate.Release(); }
    }
    static IEnumerable<string> RedactSteamArgs(string[] arguments)
    {
        var redacting = false;
        foreach (var argument in arguments)
        {
            if (argument == "+login") { redacting = true; yield return argument; continue; }
            if (redacting && argument.StartsWith('+')) redacting = false;
            yield return redacting ? "***" : argument;
        }
    }
    Process StartProcess(string file, IEnumerable<string> arguments, string workingDirectory, Action<string>? observe = null, string source = "manager", string? runId = null)
    {
        var child = new Process { StartInfo = new(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }, EnableRaisingEvents = true };
        foreach (var argument in arguments) child.StartInfo.ArgumentList.Add(argument);
        child.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) { observe?.Invoke(eventArgs.Data); Push("stdout", eventArgs.Data, source, runId); } };
        child.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) { observe?.Invoke(eventArgs.Data); Push("stderr", eventArgs.Data, source, runId); } };
        child.Start(); child.BeginOutputReadLine(); child.BeginErrorReadLine();
        return child;
    }
    public LogEntry Push(string type, string data, string source = "manager", string? runId = null) =>
        logHub.Push(type, data, source, runId);
}

/// <summary>Interactive SteamCMD login session including Steam Guard input.</summary>
public sealed class SteamCmdSession(ServerPaths paths)
{
    Process? process;
    string? username;
    bool awaitingInput;
    string? lastError;
    readonly List<LogEntry> logs = [];
    public bool IsRunning => process is { HasExited: false };
    public static object EmptyPublicState() => new { running = false, awaitingInput = false, username = (string?)null, exitCode = (int?)null, lastError = (string?)null, logs = Array.Empty<LogEntry>() };
    public static bool HasCachedLogin(string? expectedUsername = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[] { Path.Combine(home, "Steam", "config", "loginusers.vdf"), Path.Combine(home, "Steam", "config", "config.vdf"), Path.Combine(home, ".steam", "steam", "config", "loginusers.vdf"), Path.Combine(home, ".steam", "steam", "config", "config.vdf") };
        foreach (var candidate in candidates.Where(File.Exists))
        {
            string text;
            try { text = File.ReadAllText(candidate); } catch { continue; }
            if (!string.IsNullOrWhiteSpace(expectedUsername) && text.Contains(expectedUsername, StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains("loginkey", StringComparison.OrdinalIgnoreCase) || text.Contains("refreshtoken", StringComparison.OrdinalIgnoreCase) || text.Contains("\"accounts\"", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    public static void ResetCache()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var directory in new[] { Path.Combine(home, "Steam"), Path.Combine(home, ".steam") }) ClearDirectory(directory);
    }
    static void ClearDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        var full = Path.GetFullPath(directory);
        var home = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var boundary = home.EndsWith(Path.DirectorySeparatorChar) ? home : home + Path.DirectorySeparatorChar;
        if (!full.StartsWith(boundary, StringComparison.Ordinal)) throw new InvalidOperationException("Refusing to reset SteamCMD outside the user home");
        foreach (var file in Directory.EnumerateFiles(full)) File.Delete(file);
        foreach (var child in Directory.EnumerateDirectories(full)) Directory.Delete(child, true);
    }
    public Task<object> PublicStateAsync(bool includeLogs = false) => Task.FromResult<object>(new { running = process is { HasExited: false }, awaitingInput, username, exitCode = process?.HasExited == true ? process.ExitCode : (int?)null, lastError, logs = includeLogs ? logs : [] });
    public Task StartAsync(string user, string password)
    {
        if (process is { HasExited: false }) throw new InvalidOperationException("SteamCMD login is already running");
        username = user; awaitingInput = false; lastError = null; logs.Clear();
        var arguments = new List<string> { "+login", user };
        if (!string.IsNullOrWhiteSpace(password)) arguments.Add(password);
        arguments.Add("+quit");
        process = new Process { StartInfo = new(paths.SteamCmd) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }, EnableRaisingEvents = true };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) Push("stdout", eventArgs.Data); };
        process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) Push("stderr", eventArgs.Data); };
        process.Exited += (_, _) => awaitingInput = false;
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        return Task.CompletedTask;
    }
    public void Write(string input) { if (process is { HasExited: false }) { process.StandardInput.WriteLine(input); awaitingInput = false; } }
    public void Cancel() { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
    void Push(string type, string data)
    {
        logs.Add(new(type, data, DateTimeOffset.UtcNow, Source: "steamcmd"));
        if (logs.Count > 300) logs.RemoveAt(0);
        var text = data.ToLowerInvariant();
        if (text.Contains("steam guard") || text.Contains("two-factor") || text.Contains("auth code") || text.Contains("password:")) awaitingInput = true;
        if (text.Contains("logged in ok") || text.Contains("update state") || text.Contains("unloading steam api")) awaitingInput = false;
        if (text.Contains("error") || text.Contains("invalid password") || text.Contains("failed")) lastError = data;
    }
}
