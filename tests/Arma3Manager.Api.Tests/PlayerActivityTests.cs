using Arma3Manager.Api.Application;
using Arma3Manager.Api.Configuration;
using Arma3Manager.Api.Contracts;
using Arma3Manager.Api.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
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
        Assert.DoesNotContain(ServerCfgInstrumentation.LegacyMarker, twice);
        Assert.DoesNotContain("diag_log format", twice);
        Assert.DoesNotContain("getUserInfo", twice);
        Assert.True(File.Exists(path + ".a3mgr.bak"));
    }

    [Fact]
    public async Task ServerCfgInstrumentationUpgradesBrokenV1Hook()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "server.cfg");
        await File.WriteAllTextAsync(path, "onUserConnected = \"diag_log format [\"\"A3MGR_PLAYER_V1|CONNECTED|%1\"\",_this];customCommand _this\";");

        var result = await ServerCfgInstrumentation.ApplyAsync(path);
        var text = await File.ReadAllTextAsync(path);

        Assert.True(result.Complete);
        Assert.DoesNotContain("diag_log format", text);
        Assert.DoesNotContain(ServerCfgInstrumentation.LegacyMarker, text);
        Assert.Contains("A3MGR_PLAYER_V3|CONNECTED|", text);
        Assert.Contains("customCommand _this", text);
    }

    [Fact]
    public async Task ServerCfgInstrumentationRemovesInvalidV2GetUserInfoHook()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "server.cfg");
        await File.WriteAllTextAsync(path,
            "onUserConnected = \"diag_log (\"\"A3MGR_PLAYER_V2|CONNECTED|\"\" + str (_this + [getUserInfo (_this select 0)]));customCommand _this\";");

        var result = await ServerCfgInstrumentation.ApplyAsync(path);
        var once = await File.ReadAllTextAsync(path);
        await ServerCfgInstrumentation.ApplyAsync(path);
        var twice = await File.ReadAllTextAsync(path);

        Assert.True(result.Complete);
        Assert.Equal(once, twice);
        Assert.DoesNotContain(ServerCfgInstrumentation.InvalidV2Marker, twice);
        Assert.DoesNotContain("getUserInfo", twice);
        Assert.Contains("A3MGR_PLAYER_V3|CONNECTED|", twice);
        Assert.Contains("customCommand _this", twice);
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
    public void ParsesKickReasonAsInferredUntilServerTypeIdsAreValidated()
    {
        var entry = new LogEntry("rpt", "14:05:00 A3MGR_PLAYER_V1|KICKED|[\"42\",4,\"Missing addon ace_main\"]", DateTimeOffset.UtcNow, Source: "arma", RunId: "run-1");

        Assert.True(PlayerSignalParser.TryParse(entry, out var signal));
        Assert.Equal("kicked", signal!.Kind);
        Assert.Equal("42", signal.NetworkId);
        Assert.Equal("missing_addon", signal.ReasonCode);
        Assert.Equal("Missing addon ace_main", signal.ReasonText);
        Assert.Equal("inferred", signal.Confidence);
        Assert.Equal(entry.Data, signal.RawText);
    }

    [Fact]
    public void RejectsArmaExpressionErrorContainingManagedMarker()
    {
        var entry = new LogEntry("stderr",
            "21:37:15 Error in expression <diag_log format [\"A3MGR_PLAYER_V1|CONNECTED|%1\",_this];>",
            DateTimeOffset.UtcNow, Source: "arma", RunId: "run-1");

        Assert.False(PlayerSignalParser.TryParse(entry, out var signal));
        Assert.Null(signal);
    }

    [Fact]
    public void ParsesV2ConnectionWithSteamIdentity()
    {
        var entry = new LogEntry("stdout",
            "21:37:15 \"A3MGR_PLAYER_V2|CONNECTED|[\"42\",[\"42\",3,\"76561198000000000\",\"Alpha\",\"Alpha\",\"local\",10,false,0,[45,1000,0],objNull]]\"",
            DateTimeOffset.UtcNow, Source: "arma", RunId: "run-1");

        Assert.True(PlayerSignalParser.TryParse(entry, out var signal));
        Assert.Equal("42", signal!.NetworkId);
        Assert.Equal("76561198000000000", signal.SteamUid);
        Assert.Equal("Alpha", signal.Name);
        Assert.Equal("authoritative", signal.Confidence);
    }

    [Fact]
    public void ParsesNaturalConnectionFromRealArmaLogWithSteamIdentity()
    {
        var entry = new LogEntry("rpt", "22:52:33 Player DDI.NOVEMBER connected (id=76561198074208173).",
            DateTimeOffset.UtcNow, Source: "arma", RunId: "run-1");

        Assert.True(PlayerSignalParser.TryParse(entry, out var signal));
        Assert.Equal("connected", signal!.Kind);
        Assert.Equal("DDI.NOVEMBER", signal.Name);
        Assert.Equal("76561198074208173", signal.SteamUid);
        Assert.Null(signal.NetworkId);
        Assert.True(signal.Admitted);
        Assert.Equal("authoritative", signal.Confidence);
    }

    [Fact]
    public async Task NaturalDisconnectClosesConnectionBySteamUid()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(directory.Path, "manager.sqlite3"));
        await store.InitAsync();
        var started = DateTimeOffset.UtcNow;
        await store.StartServerSessionAsync(new("run-1", started, 123));
        var connectedEntry = new LogEntry("rpt", "22:52:33 Player Name With Spaces connected (id=76561198074208173).",
            started.AddSeconds(1), Source: "arma", RunId: "run-1");
        var disconnectedEntry = connectedEntry with
        {
            Data = "23:01:02 Player Name With Spaces disconnected (id=76561198074208173).",
            Ts = started.AddMinutes(9)
        };

        Assert.True(PlayerSignalParser.TryParse(connectedEntry, out var connected));
        Assert.True(PlayerSignalParser.TryParse(disconnectedEntry, out var disconnected));
        await store.ApplyPlayerSignalsAsync([connected!, disconnected!]);

        var connection = Assert.Single((await store.GetPlayerConnectionsAsync("run-1", null, null, null, null, 50)).Items);
        Assert.False(connection.Active);
        Assert.Equal("76561198074208173", connection.SteamUid);
        Assert.Equal(2, (await store.GetPlayerEventsAsync(connection.Id)).Count);
    }

    [Fact]
    public async Task TrackingStateTreatsDisabledBattlEyeAsCompleteIdentityTracking()
    {
        using var directory = new TemporaryDirectory();
        var configFile = Path.Combine(directory.Path, "manager.toml");
        await File.WriteAllTextAsync(configFile,
            $"[web]\npassword=\"secure-password\"\nsession_secret=\"01234567890123456789012345678901\"\n" +
            $"[server]\narma3_dir=\"{directory.Path}\"\nrcon_port=2301\nrcon_password=\"rcon-secret\"\n");
        var config = AppConfig.LoadFiles(configFile);
        var logHub = new LogHub();
        var runtime = new RuntimeState(logHub);
        var store = new SqliteStore(Path.Combine(directory.Path, "manager.sqlite3"));
        await using var rcon = new BattlEyeRconClient(config);
        using var service = new PlayerActivityService(NullLogger<PlayerActivityService>.Instance, config, runtime, logHub, rcon, store);
        service.ReportInstrumentation(new(true, ["onUserConnected"], []));

        service.ConfigureForRun(false);
        var identity = service.State;
        Assert.Equal("full", identity.Mode);
        Assert.Equal("log_identity", identity.Profile);
        Assert.Contains("steamUid", identity.AvailableFields!);
        Assert.DoesNotContain("ip", identity.AvailableFields!);
        Assert.Null(identity.LastError);

        service.ConfigureForRun(true);
        var enriched = service.State;
        Assert.Equal("partial", enriched.Mode);
        Assert.Equal("battleye_enriched", enriched.Profile);
        Assert.Contains("ip", enriched.AvailableFields!);
        Assert.Contains("battlEyeGuid", enriched.AvailableFields!);
    }

    [Fact]
    public void ParsesV2ConnectionAsEscapedRptString()
    {
        var entry = new LogEntry("rpt",
            "21:37:15 \"A3MGR_PLAYER_V2|CONNECTED|[\"\"42\"\",[\"\"42\"\",3,\"\"76561198000000000\"\",\"\"Alpha\"\",\"\"Alpha\"\",\"\"local\"\",10,false,0,[45,1000,0],objNull]]\"",
            DateTimeOffset.UtcNow, Source: "arma", RunId: "run-1");

        Assert.True(PlayerSignalParser.TryParse(entry, out var signal));
        Assert.Equal("42", signal!.NetworkId);
        Assert.Equal("76561198000000000", signal.SteamUid);
        Assert.Equal("Alpha", signal.Name);
    }

    [Fact]
    public void RejectsHeadlessAndMalformedStructuredConnections()
    {
        var headless = new LogEntry("stdout",
            "A3MGR_PLAYER_V2|CONNECTED|[\"42\",[\"42\",3,\"HC12160\",\"HC1\",\"HC1\",\"local\",10,true]]",
            DateTimeOffset.UtcNow, Source: "arma", RunId: "run-1");
        var malformed = new LogEntry("stdout", "A3MGR_PLAYER_V2|CONNECTED|[\"%1\",_this]",
            DateTimeOffset.UtcNow, Source: "arma", RunId: "run-1");

        Assert.False(PlayerSignalParser.TryParse(headless, out _));
        Assert.False(PlayerSignalParser.TryParse(malformed, out _));
    }

    [Fact]
    public void PreservesAnonymousWrongPasswordRejection()
    {
        var entry = new LogEntry("stderr", "21:37:15 Cannot join the session. Wrong password was given.",
            DateTimeOffset.UtcNow, Source: "arma", RunId: "run-1");

        Assert.True(PlayerSignalParser.TryParse(entry, out var signal));
        Assert.Equal("rejected", signal!.Kind);
        Assert.Equal("wrong_password", signal.ReasonCode);
        Assert.Null(signal.NetworkId);
        Assert.Equal("inferred", signal.Confidence);
    }

    [Fact]
    public void IgnoresHeadlessClientWrongPasswordButKeepsServerRejection()
    {
        const string message = "21:37:15 Cannot join the session. Wrong password was given.";
        var headless = new LogEntry("stderr", message, DateTimeOffset.UtcNow, Source: "headless-client-1", RunId: "run-1");
        var server = headless with { Source = "arma" };

        Assert.False(PlayerSignalParser.TryParse(headless, out _));
        Assert.True(PlayerSignalParser.TryParse(server, out var signal));
        Assert.Equal("wrong_password", signal!.ReasonCode);
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

    [Fact]
    public async Task RconRosterEnrichesHookConnectionThatAlreadyHasSteamIdentity()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(directory.Path, "manager.sqlite3"));
        await store.InitAsync();
        var started = DateTimeOffset.UtcNow;
        await store.StartServerSessionAsync(new("run-1", started, 123));

        await store.ApplyPlayerSignalAsync(new PlayerSignal(
            "run-1", started.AddSeconds(1), "connected", "server_hooks", "authoritative",
            "hook connected", "hook-1", NetworkId: "42", SteamUid: "76561198000000000", Name: "Alpha", Admitted: true));
        await store.ApplyPlayerSignalAsync(new PlayerSignal(
            "run-1", started.AddSeconds(5), "rcon_seen", "rcon_roster", "authoritative",
            "rcon verified", "rcon-1", BattlEyeGuid: "0123456789abcdef", RconPlayerId: 7,
            Name: "Alpha", Ip: "203.0.113.7", Admitted: true));

        var connection = Assert.Single((await store.GetPlayerConnectionsAsync("run-1", null, null, null, null, 50)).Items);
        Assert.Equal("76561198000000000", connection.SteamUid);
        Assert.Equal("0123456789abcdef", connection.BattlEyeGuid);
        Assert.Equal("203.0.113.7", connection.Ip);
    }

    [Fact]
    public async Task V2MigrationRemovesFalseExpressionConnections()
    {
        using var directory = new TemporaryDirectory();
        var dbPath = Path.Combine(directory.Path, "manager.sqlite3");
        var store = new SqliteStore(dbPath);
        await store.InitAsync();
        var started = DateTimeOffset.UtcNow;
        await store.StartServerSessionAsync(new("run-1", started, 123));
        await store.ApplyPlayerSignalAsync(new PlayerSignal(
            "run-1", started.AddSeconds(1), "connected", "arma", "authoritative",
            "Error in expression <diag_log format [\"A3MGR_PLAYER_V1|CONNECTED|%1\",_this];>",
            "bad-event", NetworkId: "%1,_this];>", Admitted: true));

        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            var resetMigration = connection.CreateCommand();
            resetMigration.CommandText = "delete from schema_migrations where version=2";
            await resetMigration.ExecuteNonQueryAsync();
        }

        await store.InitAsync();

        Assert.Empty((await store.GetPlayerConnectionsAsync("run-1", null, null, null, null, 50)).Items);
    }

    [Fact]
    public async Task V3MigrationRemovesOnlyAnonymousHeadlessPasswordArtifact()
    {
        using var directory = new TemporaryDirectory();
        var dbPath = Path.Combine(directory.Path, "manager.sqlite3");
        var store = new SqliteStore(dbPath);
        await store.InitAsync();
        var started = DateTimeOffset.UtcNow;
        await store.StartServerSessionAsync(new("run-1", started, 123));
        await store.ApplyPlayerSignalAsync(new PlayerSignal(
            "run-1", started.AddSeconds(1), "rejected", "arma", "inferred",
            "22:18:05 Cannot join the session. Wrong password was given.", "hc-artifact",
            ReasonCode: "wrong_password", ReasonText: "Cannot join the session. Wrong password was given.", Terminal: true));
        await store.ApplyPlayerSignalAsync(new PlayerSignal(
            "run-1", started.AddSeconds(2), "rejected", "arma", "inferred",
            "Cannot join the session. Wrong password was given.", "identified-player",
            SteamUid: "76561198000000000", Name: "Alpha", ReasonCode: "wrong_password", Terminal: true));
        await store.ApplyPlayerSignalAsync(new PlayerSignal(
            "run-1", started.AddSeconds(3), "rejected", "arma", "inferred",
            "Password validation failed for another reason", "different-message",
            ReasonCode: "wrong_password", ReasonText: "Password validation failed for another reason", Terminal: true));

        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            var resetMigration = connection.CreateCommand();
            resetMigration.CommandText = "delete from schema_migrations where version=3";
            await resetMigration.ExecuteNonQueryAsync();
        }

        await store.InitAsync();

        var remaining = (await store.GetPlayerConnectionsAsync("run-1", null, null, null, null, 50)).Items;
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, connection => connection.SteamUid == "76561198000000000");
        Assert.Contains(remaining, connection => connection.ReasonText == "Password validation failed for another reason");
    }

    [Fact]
    public async Task V4MigrationBackfillsNaturalSteamIdentityWithoutOverwritingName()
    {
        using var directory = new TemporaryDirectory();
        var dbPath = Path.Combine(directory.Path, "manager.sqlite3");
        var store = new SqliteStore(dbPath);
        await store.InitAsync();
        var started = DateTimeOffset.UtcNow;
        await store.StartServerSessionAsync(new("run-1", started, 123));
        await store.ApplyPlayerSignalAsync(new PlayerSignal(
            "run-1", started.AddSeconds(1), "connected", "arma", "inferred",
            "22:52:33 Player DDI.NOVEMBER connected (id=76561198074208173).", "legacy-natural",
            Name: "Preserved Name", Admitted: true));

        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            var resetMigration = connection.CreateCommand();
            resetMigration.CommandText = "delete from schema_migrations where version=4";
            await resetMigration.ExecuteNonQueryAsync();
        }

        await store.InitAsync();

        var repaired = Assert.Single((await store.GetPlayerConnectionsAsync("run-1", null, null, null, null, 50)).Items);
        Assert.Equal("76561198074208173", repaired.SteamUid);
        Assert.Equal("Preserved Name", repaired.Name);
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
