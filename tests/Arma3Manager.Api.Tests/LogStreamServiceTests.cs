using Arma3Manager.Api.Application;
using Arma3Manager.Api.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using Xunit;

namespace Arma3Manager.Api.Tests;

public sealed class LogStreamServiceTests
{
    [Fact]
    public async Task StreamStartsWithHeartbeatStatusAndResumableLog()
    {
        var hub = new LogHub(10);
        var runtime = new RuntimeState(hub);
        var entry = runtime.Push("stdout", "ready", "arma", "run-1");
        var service = new LogStreamService(runtime, NullLogger<LogStreamService>.Instance);
        using var cancellation = new CancellationTokenSource();
        await using var stream = service.Stream(0, cancellation.Token).GetAsyncEnumerator();

        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("heartbeat", stream.Current.EventType);
        Assert.Equal(TimeSpan.FromSeconds(3), stream.Current.ReconnectionInterval);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("status", stream.Current.EventType);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("message", stream.Current.EventType);
        Assert.Equal(entry.Id.ToString(), stream.Current.EventId);
        Assert.Equal(entry, Assert.IsType<LogEntry>(stream.Current.Data));

        cancellation.Cancel();
    }

    [Fact]
    public async Task StreamReportsGapBeforeSendingRetainedEntries()
    {
        var hub = new LogHub(3);
        var runtime = new RuntimeState(hub);
        var first = runtime.Push("stdout", "line-1", "arma", "run-1");
        runtime.Push("stdout", "line-2", "arma", "run-1");
        runtime.Push("stdout", "line-3", "arma", "run-1");
        runtime.Push("stdout", "line-4", "arma", "run-1");
        runtime.Push("stdout", "line-5", "arma", "run-1");
        var service = new LogStreamService(runtime, NullLogger<LogStreamService>.Instance);
        using var cancellation = new CancellationTokenSource();
        await using var stream = service.Stream(first.Id, cancellation.Token).GetAsyncEnumerator();

        Assert.True(await stream.MoveNextAsync()); // heartbeat
        Assert.True(await stream.MoveNextAsync()); // status
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("gap", stream.Current.EventType);
        var gap = Assert.IsType<LogGapEvent>(stream.Current.Data);
        Assert.Equal(first.Id, gap.RequestedId);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("line-3", Assert.IsType<LogEntry>(stream.Current.Data).Data);

        cancellation.Cancel();
    }

    [Fact]
    public async Task ReconnectCursorSkipsAlreadyDeliveredEntries()
    {
        var hub = new LogHub(10);
        var runtime = new RuntimeState(hub);
        var old = runtime.Push("stdout", "old", "arma", "run-1");
        var fresh = runtime.Push("stdout", "fresh", "arma", "run-1");
        var service = new LogStreamService(runtime, NullLogger<LogStreamService>.Instance);
        using var cancellation = new CancellationTokenSource();
        await using var stream = service.Stream(old.Id, cancellation.Token).GetAsyncEnumerator();

        Assert.True(await stream.MoveNextAsync()); // heartbeat
        Assert.True(await stream.MoveNextAsync()); // status
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(fresh, Assert.IsType<LogEntry>(stream.Current.Data));

        cancellation.Cancel();
    }

    [Fact]
    public async Task BatchedStreamGroupsReplayIntoAtMostOneHundredEntries()
    {
        var hub = new LogHub(300);
        var runtime = new RuntimeState(hub);
        for (var index = 0; index < 205; index++) runtime.Push("stdout", $"line-{index}");
        var service = new LogStreamService(runtime, NullLogger<LogStreamService>.Instance);
        using var cancellation = new CancellationTokenSource();
        await using var stream = service.Stream(0, cancellation.Token, batch: true).GetAsyncEnumerator();

        Assert.True(await stream.MoveNextAsync()); // heartbeat
        Assert.True(await stream.MoveNextAsync()); // status
        var sizes = new List<int>();
        for (var index = 0; index < 3; index++)
        {
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal("logs", stream.Current.EventType);
            sizes.Add(Assert.IsType<LogBatchEvent>(stream.Current.Data).Entries.Count);
        }

        Assert.Equal([100, 100, 5], sizes);
        cancellation.Cancel();
    }

    [Fact]
    public async Task LiveBatchCoalescesEntriesWithinTheFiftyMillisecondWindow()
    {
        var hub = new LogHub(20);
        var runtime = new RuntimeState(hub);
        var service = new LogStreamService(runtime, NullLogger<LogStreamService>.Instance);
        using var cancellation = new CancellationTokenSource();
        await using var stream = service.Stream(0, cancellation.Token, batch: true).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync()); // heartbeat
        Assert.True(await stream.MoveNextAsync()); // status

        var pending = stream.MoveNextAsync().AsTask();
        await Task.Delay(10);
        runtime.Push("stdout", "one");
        runtime.Push("stdout", "two");

        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, Assert.IsType<LogBatchEvent>(stream.Current.Data).Entries.Count);
        cancellation.Cancel();
    }

    [Fact]
    public async Task BatchedStreamRespectsThePayloadBudget()
    {
        var hub = new LogHub(10);
        var runtime = new RuntimeState(hub);
        for (var index = 0; index < 5; index++) runtime.Push("stdout", new string((char)('a' + index), 60_000));
        var service = new LogStreamService(runtime, NullLogger<LogStreamService>.Instance);
        using var cancellation = new CancellationTokenSource();
        await using var stream = service.Stream(0, cancellation.Token, batch: true).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.True(await stream.MoveNextAsync());

        var received = 0;
        while (received < 5)
        {
            Assert.True(await stream.MoveNextAsync());
            var entries = Assert.IsType<LogBatchEvent>(stream.Current.Data).Entries;
            Assert.True(entries.Sum(entry => Encoding.UTF8.GetByteCount(entry.Data) + 256) <= LogStreamService.MaxBatchBytes);
            received += entries.Count;
        }
        cancellation.Cancel();
    }

    [Fact]
    public async Task SubscriberOverflowProducesGapAndNewestReplay()
    {
        var hub = new LogHub(3);
        var runtime = new RuntimeState(hub);
        var service = new LogStreamService(runtime, NullLogger<LogStreamService>.Instance);
        using var cancellation = new CancellationTokenSource();
        await using var stream = service.Stream(0, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync()); // heartbeat and subscription registration
        Assert.True(await stream.MoveNextAsync()); // status

        for (var index = 0; index < LogHub.DefaultSubscriptionCapacity + 10; index++)
            runtime.Push("stdout", $"line-{index}");

        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("gap", stream.Current.EventType);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal($"line-{LogHub.DefaultSubscriptionCapacity + 7}", Assert.IsType<LogEntry>(stream.Current.Data).Data);
        cancellation.Cancel();
    }
}
