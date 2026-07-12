using Arma3Manager.Api.Infrastructure;
using Arma3Manager.Api.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arma3Manager.Api.Tests;

public sealed class FileIndexerTests
{
    [Fact]
    public async Task FirstScanIndexesFilesAndComputesRecursiveDirectorySizes()
    {
        using var tree = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tree.Path, "server.cfg"), "hostname=test"); // 13 bytes
        var modDir = Directory.CreateDirectory(Path.Combine(tree.Path, "@mymod"));
        var addonsDir = Directory.CreateDirectory(Path.Combine(modDir.FullName, "addons"));
        File.WriteAllText(Path.Combine(addonsDir.FullName, "mod.pbo"), "0123456789"); // 10 bytes

        using var db = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(db.Path, "manager.sqlite3"));
        await store.InitAsync();

        await FileIndexScanner.ScanAsync(tree.Path, store, CancellationToken.None);

        var root = await store.GetFileIndexChildrenAsync("");
        Assert.NotNull(root);
        Assert.Equal(2, root!.Length); // exactly server.cfg and @mymod — root's own self-referential row must not leak in as its own child
        var cfg = root!.Single(i => i.Name == "server.cfg");
        Assert.Equal(13, cfg.Size);
        Assert.False(cfg.IsDir);

        var mod = root!.Single(i => i.Name == "@mymod");
        Assert.True(mod.IsDir);
        Assert.Equal(10, mod.Size); // recursive rollup through addons/

        Assert.Equal(23, await store.GetIndexedRootSizeAsync());
    }

    [Fact]
    public async Task ManagerDatabaseFilesAreNeverIndexed()
    {
        using var tree = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(tree.Path, "manager.sqlite3"));
        await store.InitAsync();
        File.WriteAllText(Path.Combine(tree.Path, "keep.txt"), "hi");

        await FileIndexScanner.ScanAsync(tree.Path, store, CancellationToken.None);

        var root = await store.GetFileIndexChildrenAsync("");
        Assert.NotNull(root);
        Assert.DoesNotContain(root!, i => i.Name.StartsWith("manager.sqlite3"));
        Assert.Contains(root!, i => i.Name == "keep.txt");
    }

    [Fact]
    public async Task UnchangedDirectoryIsNotReEnumeratedOnTheNextScan()
    {
        using var tree = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tree.Path, "note.txt"), "hello");
        using var db = new TemporaryDirectory();
        var dbPath = Path.Combine(db.Path, "manager.sqlite3");
        var store = new SqliteStore(dbPath);
        await store.InitAsync();

        await FileIndexScanner.ScanAsync(tree.Path, store, CancellationToken.None);
        var firstGen = await GetScanGenAsync(dbPath, "note.txt");

        await FileIndexScanner.ScanAsync(tree.Path, store, CancellationToken.None);
        var secondGen = await GetScanGenAsync(dbPath, "note.txt");

        // The root's mtime didn't change between scans, so note.txt's row was never re-touched.
        Assert.Equal(firstGen, secondGen);
    }

    [Fact]
    public async Task AddingAFileUpdatesTheParentDirectoryRollupOnTheNextScan()
    {
        using var tree = new TemporaryDirectory();
        var modDir = Directory.CreateDirectory(Path.Combine(tree.Path, "@mymod"));
        File.WriteAllText(Path.Combine(modDir.FullName, "a.pbo"), "12345"); // 5 bytes
        using var db = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(db.Path, "manager.sqlite3"));
        await store.InitAsync();

        await FileIndexScanner.ScanAsync(tree.Path, store, CancellationToken.None);
        Assert.Equal(5, await store.GetIndexedRootSizeAsync());

        File.WriteAllText(Path.Combine(modDir.FullName, "b.pbo"), "1234567890"); // +10 bytes; bumps @mymod's mtime
        await FileIndexScanner.ScanAsync(tree.Path, store, CancellationToken.None);

        Assert.Equal(15, await store.GetIndexedRootSizeAsync());
    }

    [Fact]
    public async Task DeletingAFileIsReconciledOnceItsParentIsReScanned()
    {
        using var tree = new TemporaryDirectory();
        var modDir = Directory.CreateDirectory(Path.Combine(tree.Path, "@mymod"));
        var doomed = Path.Combine(modDir.FullName, "old.pbo");
        File.WriteAllText(doomed, "12345");
        File.WriteAllText(Path.Combine(modDir.FullName, "keep.pbo"), "1234567890");
        using var db = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(db.Path, "manager.sqlite3"));
        await store.InitAsync();

        await FileIndexScanner.ScanAsync(tree.Path, store, CancellationToken.None);
        Assert.Equal(15, await store.GetIndexedRootSizeAsync());

        File.Delete(doomed); // bumps @mymod's mtime, so it will be re-enumerated on the next scan
        await FileIndexScanner.ScanAsync(tree.Path, store, CancellationToken.None);

        Assert.Equal(10, await store.GetIndexedRootSizeAsync());
        var modChildren = await store.GetFileIndexChildrenAsync("@mymod");
        Assert.DoesNotContain(modChildren!, i => i.Name == "old.pbo");
    }

    [Fact]
    public async Task SymbolicLinkContentIsNotCountedTwice()
    {
        using var tree = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(tree.Path, "steamapps", "workshop", "content", "107410", "123456789"));
        File.WriteAllText(Path.Combine(source.FullName, "mod.pbo"), "0123456789");
        Directory.CreateSymbolicLink(Path.Combine(tree.Path, "@123456789"), source.FullName);
        using var db = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(db.Path, "manager.sqlite3"));
        await store.InitAsync();

        await FileIndexScanner.ScanAsync(tree.Path, store, CancellationToken.None);

        Assert.Equal(10, await store.GetIndexedRootSizeAsync());
        var root = await store.GetFileIndexChildrenAsync("");
        Assert.Equal(0, root!.Single(item => item.Name == "@123456789").Size);
        var workshopSizes = await store.GetIndexedDirectorySizesAsync("steamapps/workshop/content/107410");
        Assert.Equal(10, workshopSizes["123456789"]);
        var displaySizes = await store.GetRootDisplayDirectorySizesAsync();
        Assert.Equal(10, displaySizes["@123456789"]);
        Assert.True(displaySizes["steamapps"] >= 10);
    }

    [Fact]
    public async Task ExplicitPathRemovalImmediatelyClearsLinkAndWorkshopRows()
    {
        using var tree = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(tree.Path, "steamapps", "workshop", "content", "107410", "123456789"));
        File.WriteAllText(Path.Combine(source.FullName, "mod.pbo"), "content");
        Directory.CreateSymbolicLink(Path.Combine(tree.Path, "@123456789"), source.FullName);
        using var db = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(db.Path, "manager.sqlite3"));
        await store.InitAsync();
        await FileIndexScanner.ScanAsync(tree.Path, store, CancellationToken.None);

        await store.RemoveFileIndexForDeletedPathAsync("@123456789");
        await store.RemoveFileIndexForDeletedPathAsync("steamapps/workshop/content/107410/123456789");

        Assert.DoesNotContain((await store.GetFileIndexChildrenAsync(""))!, item => item.Name == "@123456789");
        Assert.DoesNotContain((await store.GetFileIndexChildrenAsync("steamapps/workshop/content/107410"))!, item => item.Name == "123456789");
    }

    [Fact]
    public async Task InvalidatingChangedParentsForcesAOneTimeLiveListing()
    {
        using var tree = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(tree.Path, "steamapps", "workshop", "content", "107410"));
        using var db = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(db.Path, "manager.sqlite3"));
        await store.InitAsync();
        await FileIndexScanner.ScanAsync(tree.Path, store, CancellationToken.None);

        Assert.NotNull(await store.GetFileIndexChildrenAsync(""));
        Assert.NotNull(await store.GetFileIndexChildrenAsync("steamapps/workshop/content/107410"));

        await store.InvalidateFileIndexDirAsync("");
        await store.InvalidateFileIndexDirAsync("steamapps/workshop/content/107410");

        Assert.Null(await store.GetFileIndexChildrenAsync(""));
        Assert.Null(await store.GetFileIndexChildrenAsync("steamapps/workshop/content/107410"));
    }

    [Fact]
    public async Task IndexVersionChangeClearsLegacyRowsOnlyOnce()
    {
        using var tree = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tree.Path, "large.pbo"), "content");
        using var db = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(db.Path, "manager.sqlite3"));
        await store.InitAsync();
        await FileIndexScanner.ScanAsync(tree.Path, store, CancellationToken.None);
        Assert.NotNull(await store.GetIndexedRootSizeAsync());

        Assert.True(await store.EnsureFileIndexVersionAsync(2));
        Assert.Null(await store.GetIndexedRootSizeAsync());
        await FileIndexScanner.ScanAsync(tree.Path, store, CancellationToken.None);
        Assert.False(await store.EnsureFileIndexVersionAsync(2));
        Assert.Equal(7, await store.GetIndexedRootSizeAsync());
    }

    static async Task<long> GetScanGenAsync(string dbPath, string path)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "select scan_gen from file_index where path = $path";
        command.Parameters.AddWithValue("$path", path);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"a3mgr-fileindex-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
