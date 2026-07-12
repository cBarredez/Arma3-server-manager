using Arma3Manager.Api.Infrastructure;
using Xunit;

namespace Arma3Manager.Api.Tests;

public sealed class FactoryResetExecutorTests
{
    [Fact]
    public async Task PendingResetClearsEveryRootWithoutFollowingDirectoryLinks()
    {
        using var tree = new TemporaryDirectory();
        var arma3 = Directory.CreateDirectory(Path.Combine(tree.Path, "arma3")).FullName;
        var steam = Directory.CreateDirectory(Path.Combine(tree.Path, "Steam")).FullName;
        var steamConfig = Directory.CreateDirectory(Path.Combine(tree.Path, ".steam")).FullName;
        var aspnet = Directory.CreateDirectory(Path.Combine(tree.Path, ".aspnet")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(tree.Path, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(arma3, "server.bin"), "server");
        File.WriteAllText(Path.Combine(steam, "login.vdf"), "login");
        Directory.CreateDirectory(Path.Combine(steamConfig, "config"));
        File.WriteAllText(Path.Combine(steamConfig, "config", "data"), "config");
        File.WriteAllText(Path.Combine(aspnet, "key.xml"), "key");
        Directory.CreateSymbolicLink(Path.Combine(arma3, "external-link"), outside);

        await FactoryResetExecutor.PrepareAsync(arma3);
        await FactoryResetExecutor.ExecutePendingAsync(arma3, [arma3, steam, steamConfig, aspnet]);

        Assert.Empty(Directory.EnumerateFileSystemEntries(arma3));
        Assert.Empty(Directory.EnumerateFileSystemEntries(steam));
        Assert.Empty(Directory.EnumerateFileSystemEntries(steamConfig));
        Assert.Empty(Directory.EnumerateFileSystemEntries(aspnet));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public async Task NoMarkerLeavesStorageUntouched()
    {
        using var tree = new TemporaryDirectory();
        var arma3 = Directory.CreateDirectory(Path.Combine(tree.Path, "arma3")).FullName;
        var file = Path.Combine(arma3, "keep.txt");
        File.WriteAllText(file, "keep");

        await FactoryResetExecutor.ExecutePendingAsync(arma3, [arma3]);

        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task PendingResetRefusesFilesystemRoot()
    {
        using var tree = new TemporaryDirectory();
        var arma3 = Directory.CreateDirectory(Path.Combine(tree.Path, "arma3")).FullName;
        await FactoryResetExecutor.PrepareAsync(arma3);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FactoryResetExecutor.ExecutePendingAsync(arma3, [Path.GetPathRoot(arma3)!]));

        Assert.True(File.Exists(Path.Combine(arma3, FactoryResetExecutor.MarkerName)));
    }

    [Fact]
    public async Task BrokenDirectoryLinkIsRemovedIdempotently()
    {
        using var tree = new TemporaryDirectory();
        var arma3 = Directory.CreateDirectory(Path.Combine(tree.Path, "arma3")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(tree.Path, "target")).FullName;
        var link = Path.Combine(arma3, "@workshop");
        Directory.CreateSymbolicLink(link, target);
        Directory.Delete(target);
        await FactoryResetExecutor.PrepareAsync(arma3);

        await FactoryResetExecutor.ExecutePendingAsync(arma3, [arma3]);

        Assert.False(File.Exists(link));
        Assert.False(Directory.Exists(link));
        Assert.Empty(Directory.EnumerateFileSystemEntries(arma3));
    }

    [Fact]
    public async Task PopulatedWorkshopLayoutRemovesReferencesBeforeTheirSources()
    {
        using var tree = new TemporaryDirectory();
        var arma3 = Directory.CreateDirectory(Path.Combine(tree.Path, "arma3")).FullName;
        var workshop = Directory.CreateDirectory(Path.Combine(arma3, "steamapps", "workshop", "content", "107410"));
        foreach (var id in new[] { "450814997", "1779063631", "463939057" })
        {
            var source = Directory.CreateDirectory(Path.Combine(workshop.FullName, id));
            var addons = Directory.CreateDirectory(Path.Combine(source.FullName, "addons"));
            File.WriteAllBytes(Path.Combine(addons.FullName, $"{id}.pbo"), new byte[4096]);
            Directory.CreateSymbolicLink(Path.Combine(arma3, $"@{id}"), source.FullName);
        }
        await FactoryResetExecutor.PrepareAsync(arma3);

        await FactoryResetExecutor.ExecutePendingAsync(arma3, [arma3]);

        Assert.Empty(Directory.EnumerateFileSystemEntries(arma3));
    }

    sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"a3mgr-reset-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
