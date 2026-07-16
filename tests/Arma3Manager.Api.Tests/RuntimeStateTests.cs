using Arma3Manager.Api.Application;
using Arma3Manager.Api.Contracts;
using System.Text;
using Xunit;

namespace Arma3Manager.Api.Tests;

public sealed class RuntimeStateTests
{
    [Fact]
    public async Task ASecondMainProcessCannotReplaceTheManagedProcess()
    {
        var runtime = new RuntimeState();
        var first = runtime.Start("/bin/sleep", ["30"], Path.GetTempPath());
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => runtime.Start("/bin/sleep", ["30"], Path.GetTempPath()));

            Assert.Contains(first.Pid.ToString(), error.Message);
            Assert.Equal(first.Pid, runtime.ProcessId);
        }
        finally { await runtime.StopAsync(); }
    }

    [Fact]
    public void BoundedLogKeepsStableIncreasingIdsAfterRollover()
    {
        var runtime = new RuntimeState();

        for (var index = 0; index < LogHub.DefaultCapacity + 5; index++)
            runtime.Push("stdout", $"line-{index}");

        var logs = runtime.Logs;
        Assert.Equal(LogHub.DefaultCapacity, logs.Count);
        Assert.Equal("line-5", logs[0].Data);
        Assert.Equal($"line-{LogHub.DefaultCapacity + 4}", logs[^1].Data);
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

    [Fact]
    public async Task MultipleWaitingReadersWakeForTheSameEntry()
    {
        var hub = new LogHub(10);
        var first = hub.WaitForAfterAsync(0, TimeSpan.FromSeconds(2), CancellationToken.None);
        var second = hub.WaitForAfterAsync(0, TimeSpan.FromSeconds(2), CancellationToken.None);

        var pushed = hub.Push("stdout", "broadcast", "arma", "run-1");

        Assert.Equal(pushed.Id, Assert.Single((await first).Entries).Id);
        Assert.Equal(pushed.Id, Assert.Single((await second).Entries).Id);
    }

    [Fact]
    public async Task SubscriptionAtomicallyReplaysThenReceivesLiveEntries()
    {
        var hub = new LogHub(10);
        var replay = hub.Push("stdout", "replay", "arma", "run-1");
        await using var subscription = hub.Subscribe(0);
        var live = hub.Push("stdout", "live", "arma", "run-1");

        Assert.Equal(replay.Id, Assert.Single(subscription.Initial.Entries).Id);
        Assert.Equal(live.Id, (await subscription.Reader.ReadAsync()).Id);
    }

    [Fact]
    public async Task TenSubscribersReceiveTheSameOrderedEntries()
    {
        var hub = new LogHub(20);
        var subscriptions = Enumerable.Range(0, 10).Select(_ => hub.Subscribe(0)).ToArray();
        var expected = Enumerable.Range(0, 5).Select(index => hub.Push("stdout", $"line-{index}")).Select(entry => entry.Id).ToArray();

        foreach (var subscription in subscriptions)
        {
            var actual = new List<long>();
            while (subscription.Reader.TryRead(out var entry)) actual.Add(entry.Id);
            Assert.Equal(expected, actual);
            await subscription.DisposeAsync();
        }
        Assert.Equal(0, hub.ActiveSubscriberCount);
    }

    [Fact]
    public async Task SlowSubscriberDropsOnlyItsOldestNotificationsAndRingCanRecoverThem()
    {
        var hub = new LogHub(10);
        await using var subscription = hub.Subscribe(0, capacity: 2);
        var pushed = Enumerable.Range(1, 5).Select(index => hub.Push("stdout", $"line-{index}")).ToArray();
        var queued = new List<LogEntry>();
        while (subscription.Reader.TryRead(out var entry)) queued.Add(entry);

        Assert.Equal(pushed[^2].Id, queued[0].Id);
        Assert.Equal(pushed[^1].Id, queued[1].Id);
        Assert.Equal(pushed.Select(entry => entry.Id), hub.ReadAfter(0).Entries.Select(entry => entry.Id));
    }

    [Fact]
    public void FallingBehindTheRingBufferProducesAnExplicitGap()
    {
        var hub = new LogHub(3);
        var first = hub.Push("stdout", "line-1", "arma", "run-1");
        hub.Push("stdout", "line-2", "arma", "run-1");
        hub.Push("stdout", "line-3", "arma", "run-1");
        hub.Push("stdout", "line-4", "arma", "run-1");
        hub.Push("stdout", "line-5", "arma", "run-1");

        var read = hub.ReadAfter(first.Id);

        Assert.True(read.HasGap);
        Assert.Equal(first.Id, read.RequestedId);
        Assert.Equal("line-3", read.Entries[0].Data);
        Assert.Equal("line-5", read.Entries[^1].Data);
    }

    [Fact]
    public void OversizedUtf8LineIsBoundedAndMarked()
    {
        var hub = new LogHub(2);
        var entry = hub.Push("stdout", string.Concat(Enumerable.Repeat("😀", 20_000)), "arma", "run-1");

        Assert.True(Encoding.UTF8.GetByteCount(entry.Data) <= LogHub.MaxLineBytes);
        Assert.EndsWith("[truncated at 64 KiB]", entry.Data);
        Assert.Equal("arma", entry.Source);
        Assert.Equal("run-1", entry.RunId);
    }

    [Fact]
    public async Task ConcurrentWritersKeepUniqueIncreasingIds()
    {
        var hub = new LogHub(LogHub.DefaultCapacity);
        var writers = Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
        {
            for (var line = 0; line < 500; line++) hub.Push("stdout", $"{writer}:{line}", "arma", "run-1");
        }));

        await Task.WhenAll(writers);
        var entries = hub.Snapshot();

        Assert.Equal(4_000, entries.Count);
        Assert.Equal(entries.Count, entries.Select(entry => entry.Id).Distinct().Count());
        Assert.All(entries.Zip(entries.Skip(1)), pair => Assert.True(pair.First.Id < pair.Second.Id));
    }
}
