using Arma3Manager.Api.Application;
using Arma3Manager.Api.Configuration;
using Arma3Manager.Api.Domain;
using Arma3Manager.Api.Security;
using Xunit;

namespace Arma3Manager.Api.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void PublicAndSecretTomlAreMerged()
    {
        var root = Path.Combine(Path.GetTempPath(), $"a3mgr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var publicFile = Path.Combine(root, "manager.toml");
        var secretFile = Path.Combine(root, "manager.secrets.toml");
        File.WriteAllText(publicFile, "[web]\nport = 9090\nusername = \"operator\"\n[server]\narma3_dir = \"/srv/arma3\"\n[steam]\nowner_ids = [\"123456\"]\n");
        File.WriteAllText(secretFile, "[web]\npassword = \"secret-value\"\nsession_secret = \"01234567890123456789012345678901\"\n");

        var config = AppConfig.LoadFiles(publicFile, secretFile);

        Assert.Equal(9090, config.WebPort);
        Assert.Equal("operator", config.WebUsername);
        Assert.Equal("secret-value", config.WebPassword);
        Assert.Contains("123456", config.SteamOwnerIds);
    }

    [Fact]
    public void InvalidPortFailsFast()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, "[web]\nport = 70000\n[server]\narma3_dir = \"/arma3\"\n");
        Assert.Throws<InvalidDataException>(() => AppConfig.LoadFiles(file));
    }

    [Fact]
    public void PathGuardRejectsSiblingPrefixTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "arma3");
        Assert.Throws<InvalidOperationException>(() => PathGuard.Resolve(root, Path.Combine(Path.GetTempPath(), "arma3-secrets", "secret")));
    }

    [Fact]
    public void CommandBuilderPreservesQuotedArguments()
    {
        var values = CommandBuilder.SplitArgs("-autoInit \"-name=Brigada 46\"").ToArray();
        Assert.Equal(new[] { "-autoInit", "-name=Brigada 46" }, values);
    }

    [Fact]
    public void HeadlessClientUsesServerPasswordWithoutExposingItInServerPreview()
    {
        var paths = TestPaths();
        var settings = TestSettings(paths, "two words;$HOME", 1);

        var arguments = CommandBuilder.HeadlessClientArgs(paths, settings, [], 1, Path.Combine(paths.ProfilesDir, "hc1")).ToArray();

        Assert.Contains("-password=two words;$HOME", arguments);
        Assert.DoesNotContain("password", CommandBuilder.Build(paths, settings), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HeadlessClientOmitsPasswordForOpenServer()
    {
        var paths = TestPaths();
        var arguments = CommandBuilder.HeadlessClientArgs(paths, TestSettings(paths, "", 1), [], 1, Path.Combine(paths.ProfilesDir, "hc1")).ToArray();

        Assert.DoesNotContain(arguments, argument => argument.StartsWith("-password=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HeadlessLoopbackConfigurationIsMergedAndIdempotent()
    {
        using var directory = new TemporaryDirectory();
        var paths = TestPaths(directory.Path);
        Directory.CreateDirectory(paths.ConfigDir);
        await File.WriteAllTextAsync(paths.ConfigDir + "/server.cfg", """
        password = "old";
        maxPlayers = 20;
        headlessClients[] = { "192.0.2.10" };
        localClient[] = { };
        """);
        var settings = TestSettings(paths, "protected", 2);

        await ServerCfgWriter.ApplyAsync(settings);
        var once = await File.ReadAllTextAsync(settings.ServerCfg);
        await ServerCfgWriter.ApplyAsync(settings);
        var twice = await File.ReadAllTextAsync(settings.ServerCfg);

        Assert.Equal(once, twice);
        Assert.Contains("192.0.2.10", twice);
        Assert.Equal(2, Count(twice, "127.0.0.1"));
        Assert.Contains("password = \"protected\";", twice);
    }

    [Fact]
    public async Task HeadlessLoopbackEntriesAreAddedWhenMissingAndKeptWhenDisabled()
    {
        using var directory = new TemporaryDirectory();
        var paths = TestPaths(directory.Path);
        Directory.CreateDirectory(paths.ConfigDir);
        await File.WriteAllTextAsync(paths.ConfigDir + "/server.cfg", "password = \"\";\nmaxPlayers = 20;\n");

        await ServerCfgWriter.ApplyAsync(TestSettings(paths, "", 1));
        var enabled = await File.ReadAllTextAsync(paths.ConfigDir + "/server.cfg");
        await ServerCfgWriter.ApplyAsync(TestSettings(paths, "", 0));
        var disabled = await File.ReadAllTextAsync(paths.ConfigDir + "/server.cfg");

        Assert.Contains("headlessClients[] = { \"127.0.0.1\" };", enabled);
        Assert.Contains("localClient[] = { \"127.0.0.1\" };", enabled);
        Assert.Equal(enabled, disabled);
    }

    [Fact]
    public void SessionProofRejectsAnotherSecret()
    {
        var proof = SessionProof.Create("01234567890123456789012345678901");
        Assert.True(SessionProof.Verify("01234567890123456789012345678901", proof));
        Assert.False(SessionProof.Verify("abcdefghijklmnopqrstuvwxyz123456", proof));
    }

    static ServerPaths TestPaths(string? temporaryRoot = null)
    {
        var root = temporaryRoot ?? Path.Combine(Path.GetTempPath(), $"a3mgr-config-{Guid.NewGuid():N}");
        return new(root, Path.Combine(root, "arma3server_x64"), Path.Combine(root, "steamcmd.sh"), Path.Combine(root, "config"),
            Path.Combine(root, "profiles"), Path.Combine(root, "mpmissions"), Path.Combine(root, "keys"), Path.Combine(root, "workshop"), root);
    }

    static StartupSettings TestSettings(ServerPaths paths, string password, int headlessClients) => new(
        "arma3server_x64", "0.0.0.0", 2302, paths.ProfilesDir, Path.Combine(paths.ConfigDir, "server.cfg"),
        Path.Combine(paths.ConfigDir, "basic.cfg"), "", 40, password, false, false, false, false, false,
        "", "", [], headlessClients, "");

    static int Count(string value, string needle) => (value.Length - value.Replace(needle, "").Length) / needle.Length;

    sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"a3mgr-config-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
