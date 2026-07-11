using Arma3Manager.Api.Application;
using Arma3Manager.Api.Contracts;
using Arma3Manager.Api.Domain;
using Arma3Manager.Api.Infrastructure.Persistence;

namespace Arma3Manager.Api.Infrastructure;

/// <summary>Periodically scans the Arma3 volume and keeps the SQLite file index up to date, backing the File Manager and the tracked-disk-usage metric.</summary>
public sealed class FileIndexer(SqliteStore store, ServerPaths paths, ILogger<FileIndexer> logger) : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunScanAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await RunScanAsync(stoppingToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "File index scan failed");
            }
        }
    }

    Task RunScanAsync(CancellationToken ct) => FileIndexScanner.ScanAsync(paths.Arma3Dir, store, ct);
}

/// <summary>Pure, testable incremental directory-tree scanner backing <see cref="FileIndexer"/>. Skips re-enumerating directories whose own mtime hasn't changed since the last scan, minimizing filesystem stat calls on large, mostly-static mod trees.</summary>
public static class FileIndexScanner
{
    public static async Task ScanAsync(string arma3Dir, SqliteStore store, CancellationToken ct)
    {
        var root = Path.GetFullPath(arma3Dir);
        if (!Directory.Exists(root)) return;
        var known = await store.GetIndexedDirectoriesAsync();
        var childrenByParent = known
            .Where(entry => entry.Key != entry.Value.Parent)
            .GroupBy(entry => entry.Value.Parent, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var scanGen = await store.GetNextFileIndexScanGenerationAsync();
        var upserts = new List<FileIndexRow>();
        var visitedDirs = new List<string>();
        Scan(root, "", known, childrenByParent, scanGen, upserts, visitedDirs, ct);
        await store.ApplyFileIndexScanAsync(upserts, visitedDirs, scanGen);
    }

    static long Scan(
        string fullPath,
        string relPath,
        Dictionary<string, (string Parent, DateTime MTime, long Size)> known,
        Dictionary<string, KeyValuePair<string, (string Parent, DateTime MTime, long Size)>[]> childrenByParent,
        long scanGen,
        List<FileIndexRow> upserts,
        List<string> visitedDirs,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var mtime = Directory.GetLastWriteTimeUtc(fullPath);
        var created = Directory.GetCreationTimeUtc(fullPath);
        var hadRow = known.TryGetValue(relPath, out var previous);
        long total;

        if (hadRow && previous.MTime == mtime)
        {
            // Directory's own entry list is provably unchanged (Linux updates a dir's mtime on add/remove/rename of
            // its direct children) — skip re-stat-ing files directly inside it, but still descend into known
            // subdirectories since a change deeper in the tree doesn't touch this directory's own mtime. This
            // directory is NOT added to visitedDirs: its children were never re-checked for existence, so
            // reconciliation must not sweep them just because their row wasn't touched this cycle.
            // Exclude relPath itself: the root row is self-referential (parent == path == ""), and without
            // this guard it would match here as its own "child", causing infinite recursion.
            var childDirs = childrenByParent.GetValueOrDefault(relPath) ?? [];
            var directFileSize = Math.Max(0, previous.Size - childDirs.Sum(kv => kv.Value.Size));
            total = directFileSize;
            foreach (var (childRel, _) in childDirs)
            {
                var childFull = Path.Combine(fullPath, Path.GetFileName(childRel));
                if (!Directory.Exists(childFull)) continue; // removed; will be reconciled once its true parent is re-enumerated
                total += Scan(childFull, childRel, known, childrenByParent, scanGen, upserts, visitedDirs, ct);
            }
        }
        else
        {
            visitedDirs.Add(relPath);
            total = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath))
            {
                var name = Path.GetFileName(entry);
                var entryRel = relPath.Length == 0 ? name : relPath + "/" + name;
                if (ProtectedFiles.IsProtected(entryRel)) continue;
                if (Directory.Exists(entry))
                {
                    total += Scan(entry, entryRel, known, childrenByParent, scanGen, upserts, visitedDirs, ct);
                }
                else
                {
                    var info = new FileInfo(entry);
                    upserts.Add(new FileIndexRow(entryRel, relPath, name, false, info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc, scanGen));
                    total += info.Length;
                }
            }
        }

        var parent = relPath.Length == 0 ? "" : GetParent(relPath);
        var name2 = relPath.Length == 0 ? "" : Path.GetFileName(relPath);
        upserts.Add(new FileIndexRow(relPath, parent, name2, true, total, created, mtime, scanGen));
        return total;
    }

    static string GetParent(string relPath)
    {
        var index = relPath.LastIndexOf('/');
        return index < 0 ? "" : relPath[..index];
    }
}
