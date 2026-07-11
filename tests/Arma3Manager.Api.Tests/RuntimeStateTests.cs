using Arma3Manager.Api.Application;
using Xunit;

namespace Arma3Manager.Api.Tests;

public sealed class RuntimeStateTests
{
    [Fact]
    public void BoundedLogKeepsStableIncreasingIdsAfterRollover()
    {
        var runtime = new RuntimeState();

        for (var index = 0; index < 1_005; index++)
            runtime.Push("stdout", $"line-{index}");

        var logs = runtime.Logs;
        Assert.Equal(1_000, logs.Count);
        Assert.Equal("line-5", logs[0].Data);
        Assert.Equal("line-1004", logs[^1].Data);
        Assert.All(logs.Zip(logs.Skip(1)), pair => Assert.True(pair.First.Id < pair.Second.Id));
    }

    [Fact]
    public void LogSnapshotIsNotChangedByLaterPushes()
    {
        var runtime = new RuntimeState();
        runtime.Push("stdout", "first");
        var snapshot = runtime.Logs;

        runtime.Push("stderr", "second");

        Assert.Single(snapshot);
        Assert.Equal(2, runtime.Logs.Count);
    }

    [Fact]
    public async Task WaitingReaderWakesWhenALogArrives()
    {
        var runtime = new RuntimeState();
        var wait = runtime.WaitForLogsAfterAsync(0, TimeSpan.FromSeconds(2), CancellationToken.None);

        runtime.Push("stderr", "important error");

        var logs = await wait;
        var entry = Assert.Single(logs);
        Assert.Equal("important error", entry.Data);
    }
}
