using System.Text.Json;
using Arma3Manager.Api.Configuration;

namespace Arma3Manager.Api.Infrastructure;

/// <summary>Executes an authenticated, restart-safe wipe of all manager-owned persistent storage.</summary>
public static class FactoryResetExecutor
{
    public const string MarkerName = ".factory-reset-request.json";
    public const string Confirmation = "RESET ALL ARMA3 DATA";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task PrepareAsync(string arma3Dir)
    {
        Directory.CreateDirectory(arma3Dir);
        var marker = MarkerPath(arma3Dir);
        var temporary = marker + ".tmp";
        var payload = JsonSerializer.Serialize(new ResetMarker(DateTimeOffset.UtcNow, Guid.NewGuid()), Json);
        await File.WriteAllTextAsync(temporary, payload);
        File.Move(temporary, marker, true);
    }

    public static async Task ExecutePendingAsync(AppConfig config)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await ExecutePendingAsync(config.Arma3Dir,
        [
            config.Arma3Dir,
            Path.Combine(home, "Steam"),
            Path.Combine(home, ".steam"),
            Path.Combine(home, ".aspnet")
        ]);
    }

    public static async Task ExecutePendingAsync(string arma3Dir, IEnumerable<string> roots)
    {
        var marker = MarkerPath(arma3Dir);
        if (!File.Exists(marker)) return;

        var request = JsonSerializer.Deserialize<ResetMarker>(await File.ReadAllTextAsync(marker), Json)
            ?? throw new InvalidDataException("Invalid factory reset marker");
        if (request.RequestId == Guid.Empty) throw new InvalidDataException("Invalid factory reset request ID");

        Console.WriteLine($"Factory reset {request.RequestId} started");
        foreach (var root in roots.Select(SafeRoot).Distinct(StringComparer.Ordinal))
            await ClearRootAsync(root, Path.GetFullPath(marker));

        File.Delete(marker);
        Console.WriteLine($"Factory reset {request.RequestId} completed");
    }

    static string MarkerPath(string arma3Dir) => Path.Combine(arma3Dir, MarkerName);

    static string SafeRoot(string path)
    {
        var root = Path.GetFullPath(path);
        if (string.Equals(root, Path.GetPathRoot(root), StringComparison.Ordinal))
            throw new InvalidOperationException("Factory reset refuses to clear a filesystem root");
        return root;
    }

    static async Task ClearRootAsync(string root, string marker)
    {
        if (!Directory.Exists(root)) return;
        var entries = Directory.EnumerateFileSystemEntries(root)
            .Where(entry => !string.Equals(Path.GetFullPath(entry), marker, StringComparison.Ordinal))
            .Select(entry => new { Path = entry, IsLink = IsSymbolicLink(entry) })
            .OrderByDescending(entry => entry.IsLink)
            .ToArray();
        foreach (var entry in entries)
        {
            await DeleteWithRetriesAsync(entry.Path);
        }
    }

    static bool IsSymbolicLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null || new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static async Task DeleteWithRetriesAsync(string path)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                var directory = new DirectoryInfo(path);
                if (directory.LinkTarget is not null) File.Delete(path);
                else if (directory.Exists)
                {
                    directory.Delete(true);
                }
                else
                {
                    var file = new FileInfo(path);
                    if (file.LinkTarget is not null) file.Delete();
                    else if (file.Exists)
                    {
                        file.Attributes = FileAttributes.Normal;
                        file.Delete();
                    }
                }
                return;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastError = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt));
            }
        }
        throw new IOException($"Factory reset could not delete {path}", lastError);
    }

    sealed record ResetMarker(DateTimeOffset RequestedAt, Guid RequestId);
}
