using Arma3Manager.Api.Application;
using Arma3Manager.Api.Contracts;
using Arma3Manager.Api.Infrastructure.Persistence;
using Xunit;

namespace Arma3Manager.Api.Tests;

public sealed class PlayerActivityTests
{
    [Fact]
    public async Task ServerCfgInstrumentationIsIdempotentAndPreservesExistingCommands()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "server.cfg");
        await File.WriteAllTextAsync(path, """
        onUserConnected = "";
        onUserDisconnected = "customCommand _this";
        onUnsignedData = "kick (_this select 0)";
        onHackedData = "kick (_this select 0)";
        onDifferentData = "";
        doubleIdDetected = "";
        """);

        var first = await ServerCfgInstrumentation.ApplyAsync(path);
        var once = await File.ReadAllTextAsync(path);
        var second = await ServerCfgInstrumentation.ApplyAsync(path);
        var twice = await File.ReadAllTextAsync(path);

        Assert.True(first.Complete);
        Assert.True(second.Complete);
        Assert.Equal(once, twice);
        Assert.Contains("customCommand _this", twice);
        Assert.Contains("kick (_this select 0)", twice);
        Assert.Equal(7, Count(twice, ServerCfgInstrumentation.Marker));
        Assert.True(File.Exists(path + ".a3mgr.bak"));
    }

    [Fact]
    public async Task DuplicateHandlersProducePartialInstrumentationWithoutReplacingThem()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "server.cfg");
        await File.WriteAllTextAsync(path, "onUserConnected = \"one\";\nonUserConnected = \"two\";");

        var result = await ServerCfgInstrumentation.ApplyAsync(path);

        Assert.False(result.Complete);
        Assert.Contains(result.Errors, error => error.Contains("onUserConnected"));
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("onUserConnected = \"one\"", text);
        Assert.Contains("onUserConnected = \"two\"", text);
    }

    [Fact]
    public void ParsesAuthoritativeKickReasonAndPreservesRawEvidence()
    {
        var entry = new LogEntry("rpt", "14:05:00 A3MGR_PLAYER_V1|KICKED|[\"42\",4,\"Missing addon ace_main\"]", DateTimeOffset.UtcNow, Source: "arma", RunId: "run-1");

        Assert.True(PlayerSignalParser.TryParse(entry, out var signal));
        Assert.Equal("kicked", signal!.Kind);
        Assert.Equal("42", signal.NetworkId);
        Assert.Equal("missing_addon", signal.ReasonCode);
        Assert.Equal("Missing addon ace_main", signal.ReasonText);
        Assert.Equal("authoritative", signal.Confidence);
        Assert.Equal(entry.Data, signal.RawText);
    }

    [Fact]
    public void StructuredStdoutAndQuotedRptCopiesShareADedupeFingerprint()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1_800_000_001);
        var stdout = PlayerSignalParser.Fingerprint("run-1", "kicked", "42", null, "Missing addon", "A3MGR_PLAYER_V1|KICKED|[\"42\",4,\"Missing addon\"]", at);
        var rpt = PlayerSignalParser.Fingerprint("run-1", "kicked", "42", null, "Missing addon", "14:05:00 \"A3MGR_PLAYER_V1|KICKED|[\"42\",4,\"Missing addon\"]\"", at.AddSeconds(1));

        Assert.Equal(stdout, rpt);
    }

    [Fact]
    public async Task StoreCorrelatesSignalsAndDoesNotPersistDuplicateEvidence()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(directory.Path, "manager.sqlite3"));
        await store.InitAsync();
        var started = DateTimeOffset.UtcNow;
        await store.StartServerSessionAsync(new("run-1", started, 123));
        var connected = new PlayerSignal("run-1", started.AddSeconds(1), "connected", "server_hooks", "authoritative", "connected", "event-1", NetworkId: "42", Name: "Alpha", Admitted: true);
        var rejected = new PlayerSignal("run-1", started.AddSeconds(2), "kicked", "server_hooks", "authoritative", "missing addon", "event-2", NetworkId: "42", ReasonCode: "missing_addon", ReasonText: "ace_main", Terminal: true);

        Assert.Equal(2, await store.ApplyPlayerSignalsAsync([connected, rejected]));
        Assert.False(await store.ApplyPlayerSignalAsync(rejected));

        var page = await store.GetPlayerConnectionsAsync("run-1", null, null, null, null, 50);
        var connection = Assert.Single(page.Items);
        Assert.Equal("rejected", connection.Outcome);
        Assert.False(connection.Active);
        Assert.Equal("ace_main", connection.ReasonText);
        Assert.Equal(2, (await store.GetPlayerEventsAsync(connection.Id)).Count);
        var session = await store.GetServerSessionAsync("run-1");
        Assert.Equal(1, session!.Rejected);
    }

    [Fact]
    public async Task RconRosterEnrichesASingleAnonymousHookConnection()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(directory.Path, "manager.sqlite3"));
        await store.InitAsync();
        var started = DateTimeOffset.UtcNow;
        await store.StartServerSessionAsync(new("run-1", started, 123));

        await store.ApplyPlayerSignalAsync(new PlayerSignal(
            "run-1", started.AddSeconds(1), "connected", "server_hooks", "authoritative",
            "hook connected", "hook-1", NetworkId: "42", Admitted: true));
        await store.ApplyPlayerSignalAsync(new PlayerSignal(
            "run-1", started.AddSeconds(5), "rcon_seen", "rcon_roster", "authoritative",
            "rcon verified", "rcon-1", BattlEyeGuid: "0123456789abcdef", RconPlayerId: 7,
            Name: "Alpha", Ip: "203.0.113.7", Admitted: true));

        var page = await store.GetPlayerConnectionsAsync("run-1", null, null, null, null, 50);
        var connection = Assert.Single(page.Items);
        Assert.Equal("42", connection.NetworkId);
        Assert.Equal("0123456789abcdef", connection.BattlEyeGuid);
        Assert.Equal("Alpha", connection.Name);
        Assert.Equal(2, (await store.GetPlayerEventsAsync(connection.Id)).Count);
    }

    static int Count(string value, string needle) => (value.Length - value.Replace(needle, "").Length) / needle.Length;

    sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"a3mgr-player-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
