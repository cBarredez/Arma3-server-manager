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
    public void SessionProofRejectsAnotherSecret()
    {
        var proof = SessionProof.Create("01234567890123456789012345678901");
        Assert.True(SessionProof.Verify("01234567890123456789012345678901", proof));
        Assert.False(SessionProof.Verify("abcdefghijklmnopqrstuvwxyz123456", proof));
    }
}
