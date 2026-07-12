using System.Diagnostics;
using Arma3Manager.Api.Contracts;
using Arma3Manager.Api.Domain;
using Arma3Manager.Api.Infrastructure;

namespace Arma3Manager.Api.Application;

/// <summary>Owns the Arma process, serialized maintenance tasks, and bounded in-memory logs.</summary>
public sealed class RuntimeState
{
    Process? process;
    readonly List<LogEntry> logs = [];
    readonly SemaphoreSlim taskGate = new(1, 1);
    long nextLogId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
    TaskCompletionSource<bool> logSignal = NewLogSignal();
    public IReadOnlyList<LogEntry> Logs
    {
        get { lock (logs) return logs.ToArray(); }
    }

    public async Task<IReadOnlyList<LogEntry>> WaitForLogsAfterAsync(long id, TimeSpan timeout, CancellationToken ct)
    {
        Task signal;
        lock (logs)
        {
            var pending = logs.Where(entry => entry.Id > id).ToArray();
            if (pending.Length > 0) return pending;
            signal = logSignal.Task;
        }

        try { await signal.WaitAsync(timeout, ct); }
        catch (TimeoutException) { }

        lock (logs) return logs.Where(entry => entry.Id > id).ToArray();
    }

    static TaskCompletionSource<bool> NewLogSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsRunning => process is { HasExited: false };
    public int? ProcessId => IsRunning ? process!.Id : null;

    public void Start(string file, IEnumerable<string> arguments, string workingDirectory)
    {
        process = StartProcess(file, arguments, workingDirectory);
        Push("system", $"Started {file} PID {process.Id}");
        process.Exited += (_, _) => Push("system", $"Server exited with code {process.ExitCode}");
    }
    public void Stop()
    {
        if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        Push("system", "Server stopped");
    }
    public void RunTask(string file, IEnumerable<string> arguments, string kind, Func<int, Task>? onExit = null)
    {
        var captured = arguments.ToArray();
        _ = Task.Run(() => RunTaskAsync(file, captured, kind, onExit));
    }

    public async Task<int> RunTaskAsync(string file, IEnumerable<string> arguments, string kind, Func<int, Task>? onExit = null)
    {
        var captured = arguments.ToArray();
        await taskGate.WaitAsync();
        try
        {
            using var task = StartProcess(file, captured, Directory.GetCurrentDirectory());
            Push("system", $"Task {kind} started PID {task.Id}");
            Push("system", $"Command: {CommandLog.Format(file, RedactSteamArgs(captured))}");
            await task.WaitForExitAsync();
            Push("system", $"Task {kind} exited with code {task.ExitCode}");
            if (onExit is not null) await onExit(task.ExitCode);
            return task.ExitCode;
        }
        catch (Exception exception)
        {
            Push("stderr", $"Task {kind} failed: {exception.Message}");
            return -1;
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
    Process StartProcess(string file, IEnumerable<string> arguments, string workingDirectory)
    {
        var child = new Process { StartInfo = new(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }, EnableRaisingEvents = true };
        foreach (var argument in arguments) child.StartInfo.ArgumentList.Add(argument);
        child.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) Push("stdout", eventArgs.Data); };
        child.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) Push("stderr", eventArgs.Data); };
        child.Start(); child.BeginOutputReadLine(); child.BeginErrorReadLine();
        return child;
    }
    public void Push(string type, string data)
    {
        TaskCompletionSource<bool> signal;
        lock (logs)
        {
            logs.Add(new(type, data, DateTimeOffset.UtcNow, ++nextLogId));
            if (logs.Count > 1000) logs.RemoveAt(0);
            signal = logSignal;
            logSignal = NewLogSignal();
        }
        signal.TrySetResult(true);
    }
}

/// <summary>Interactive SteamCMD login session including Steam Guard input.</summary>
public sealed class SteamCmdSession(ServerPaths paths)
{
    Process? process;
    string? username;
    bool awaitingInput;
    string? lastError;
    readonly List<LogEntry> logs = [];
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
        logs.Add(new(type, data, DateTimeOffset.UtcNow));
        if (logs.Count > 300) logs.RemoveAt(0);
        var text = data.ToLowerInvariant();
        if (text.Contains("steam guard") || text.Contains("two-factor") || text.Contains("auth code") || text.Contains("password:")) awaitingInput = true;
        if (text.Contains("logged in ok") || text.Contains("update state") || text.Contains("unloading steam api")) awaitingInput = false;
        if (text.Contains("error") || text.Contains("invalid password") || text.Contains("failed")) lastError = data;
    }
}
