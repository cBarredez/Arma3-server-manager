using Arma3Manager.Api.Application;
using Arma3Manager.Api.Configuration;
using Arma3Manager.Api.Domain;
using Arma3Manager.Api.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arma3Manager.Api.Tests;

public sealed class ServerLifecyclePersistenceTests
{
    [Fact]
    public async Task TwentyConcurrentReservationsAcceptExactlyOneStart()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(directory.Path, "manager.sqlite3"));
        await store.InitAsync();

        var attempts = Enumerable.Range(0, 20)
            .Select(index => store.TryReserveServerStartAsync($"operation-{index}", $"owner-{index}", DateTimeOffset.UtcNow));
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.Accepted);
        Assert.All(results, result => Assert.Equal("preparing", result.State.Phase));
        Assert.Equal(results.Single(result => result.Accepted).State.OperationId, (await store.GetServerRuntimeAsync()).OperationId);
    }

    [Fact]
    public async Task CompareAndSetPreventsAnOldOperationFromOverwritingTheWinner()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(directory.Path, "manager.sqlite3"));
        await store.InitAsync();
        var reserved = await store.TryReserveServerStartAsync("winner", "owner", DateTimeOffset.UtcNow);
        var current = reserved.State;

        var staleWrite = await store.UpdateServerRuntimeAsync(current with { Phase = "faulted", LastError = "stale callback" }, "old-operation");
        var winnerWrite = await store.UpdateServerRuntimeAsync(current with { Phase = "starting", Stage = "launching" }, "winner");

        Assert.False(staleWrite);
        Assert.True(winnerWrite);
        Assert.Equal("starting", (await store.GetServerRuntimeAsync()).Phase);
    }

    [Fact]
    public async Task SessionHistoryRejectsTwoOpenRunningSessions()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(directory.Path, "manager.sqlite3"));
        await store.InitAsync();
        await store.StartServerSessionAsync(new ServerRunStarted("run-1", DateTimeOffset.UtcNow, 101));

        await Assert.ThrowsAsync<SqliteException>(() =>
            store.StartServerSessionAsync(new ServerRunStarted("run-2", DateTimeOffset.UtcNow.AddSeconds(1), 102)));
    }

    [Fact]
    public async Task StopCancelsAPersistedPreparationBeforeAnyProcessLaunches()
    {
        using var directory = new TemporaryDirectory();
        var config = await CreateConfigAsync(directory.Path);
        var paths = await ServerPaths.DetectAsync(config);
        var store = new SqliteStore(Path.Combine(directory.Path, "manager.sqlite3"));
        await store.InitAsync();
        var runtime = new RuntimeState();
        using var coordinator = new ServerLifecycleCoordinator(NullLogger<ServerLifecycleCoordinator>.Instance, config, paths, store, runtime);
        await coordinator.InitializeAsync();

        var reservation = await coordinator.TryBeginStartAsync();
        var stopped = await coordinator.StopAsync();

        Assert.True(reservation.Accepted);
        Assert.Equal("preparing", reservation.Status.Phase);
        Assert.True(stopped.Accepted);
        Assert.Equal("stopped", stopped.Status.Phase);
        Assert.False(runtime.IsRunning);
    }

    static async Task<AppConfig> CreateConfigAsync(string root)
    {
        var configPath = Path.Combine(root, "manager.toml");
        await File.WriteAllTextAsync(configPath, $$"""
        [web]
        port = 8080
        username = "admin"
        password = "secure-test-password"
        session_secret = "01234567890123456789012345678901"

        [server]
        arma3_dir = "{{root}}"
        steamcmd_dir = "{{root}}"
        port = 2302
        query_port = 2303
        battleye_port = 2304
        von_port = 2305
        rcon_port = 2301
        max_players = 40
        network_mode = "bridge"
        """);
        return AppConfig.LoadFiles(configPath);
    }

    sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"a3mgr-lifecycle-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
