using Arma3Manager.Api.Application;
using Arma3Manager.Api.Configuration;
using Xunit;

namespace Arma3Manager.Api.Tests;

public sealed class WorkshopStorageTests
{
    [Fact]
    public void RepairReplacesDuplicateFolderWithLinkAndKeepsWorkshopSource()
    {
        using var root = new TemporaryDirectory();
        var config = Config(root.Path);
        var source = Directory.CreateDirectory(WorkshopStorage.Source(config, "123456789"));
        File.WriteAllText(Path.Combine(source.FullName, "mod.pbo"), "0123456789");
        var duplicate = Directory.CreateDirectory(WorkshopStorage.Reference(config, "123456789"));
        File.WriteAllText(Path.Combine(duplicate.FullName, "mod.pbo"), "0123456789");

        Assert.Equal(1, WorkshopStorage.Status(config).DuplicateCopies);
        var result = WorkshopStorage.RepairDuplicates(config);

        Assert.Equal(1, result.Converted);
        Assert.Equal(10, result.ReclaimedBytes);
        Assert.True(WorkshopStorage.IsSymbolicLink(WorkshopStorage.Reference(config, "123456789")));
        Assert.True(File.Exists(Path.Combine(source.FullName, "mod.pbo")));
    }

    [Fact]
    public void EnsureReferenceNeverCreatesASecondCopy()
    {
        using var root = new TemporaryDirectory();
        var config = Config(root.Path);
        var source = Directory.CreateDirectory(WorkshopStorage.Source(config, "987654321"));
        File.WriteAllText(Path.Combine(source.FullName, "large.pbo"), "content");

        var path = WorkshopStorage.EnsureReference(config, "987654321");

        Assert.True(path == source.FullName || WorkshopStorage.IsSymbolicLink(path));
        Assert.Single(Directory.EnumerateFiles(source.FullName));
    }

    static AppConfig Config(string root)
    {
        var file = Path.Combine(root, "manager.toml");
        File.WriteAllText(file, $"[web]\npassword = \"test-panel-password\"\nsession_secret = \"01234567890123456789012345678901\"\n[server]\narma3_dir = \"{root.Replace("\\", "/")}\"\n");
        return AppConfig.LoadFiles(file);
    }

    sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"a3mgr-workshop-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
