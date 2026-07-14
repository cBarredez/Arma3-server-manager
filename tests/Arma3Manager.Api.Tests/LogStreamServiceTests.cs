using Arma3Manager.Api.Application;
using Arma3Manager.Api.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
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
}
