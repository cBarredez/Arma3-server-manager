using Arma3Manager.Api.Application;
using Arma3Manager.Api.Domain;
using Xunit;

namespace Arma3Manager.Api.Tests;

public sealed class MissionConfigTests
{
    [Fact]
    public async Task ListsPackedAndUnpackedMissionsAndWritesTemplateWithoutPboSuffix()
    {
        using var tree = new TemporaryDirectory();
        var missions = Directory.CreateDirectory(Path.Combine(tree.Path, "mpmissions"));
        File.WriteAllText(Path.Combine(missions.FullName, "operation.test.Altis.pbo"), "mission");
        Directory.CreateDirectory(Path.Combine(missions.FullName, "training.Stratis"));
        File.WriteAllText(Path.Combine(missions.FullName, "ignore.txt"), "no");
        var serverCfg = Path.Combine(tree.Path, "server.cfg");
        await File.WriteAllTextAsync(serverCfg, """
        class Missions
        {
            class Mission1
            {
                template = "empty.VR";
                difficulty = "Custom";
            };
        };
        """);
        var paths = new ServerPaths(tree.Path, "", "", tree.Path, "", missions.FullName, "", "", tree.Path);

        var available = MissionConfig.List(paths);
        Assert.Equal(["operation.test.Altis", "training.Stratis"], available.Select(item => item.Template));

        await MissionConfig.ApplyAsync(serverCfg, "operation.test.Altis.pbo");

        Assert.Equal("operation.test.Altis", MissionConfig.ReadSelected(serverCfg));
        var content = await File.ReadAllTextAsync(serverCfg);
        Assert.Contains("template = \"operation.test.Altis\";", content);
        Assert.DoesNotContain("operation.test.Altis.pbo", content);
        Assert.Contains("difficulty = \"Custom\";", content);
    }

    sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"a3mgr-missions-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
