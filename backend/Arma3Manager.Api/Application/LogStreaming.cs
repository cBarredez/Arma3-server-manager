using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Arma3Manager.Api.Contracts;

namespace Arma3Manager.Api.Application;

public sealed record LogGapEvent(long RequestedId, long OldestAvailableId, long NewestAvailableId);
public sealed record LogHeartbeatEvent(DateTimeOffset Ts);
public sealed record LogStatusEvent(bool Running, int? Pid, string? RunId);

/// <summary>Produces resumable, typed server-sent events from the bounded runtime log history.</summary>
public sealed class LogStreamService(RuntimeState runtime, ILogger<LogStreamService> logger)
{
    static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(3);

    public async IAsyncEnumerable<SseItem<object>> Stream(
        long afterId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var connectionId = Guid.NewGuid().ToString("N")[..8];
        var lastId = Math.Max(0, afterId);
        var wasRunning = runtime.IsRunning;
        var lastRunId = runtime.RunId;
        var nextHeartbeat = DateTimeOffset.UtcNow.Add(HeartbeatInterval);
        logger.LogInformation("Log SSE {ConnectionId} opened after event {LastId}", connectionId, lastId);

        try
        {
            yield return new SseItem<object>(new LogHeartbeatEvent(DateTimeOffset.UtcNow), "heartbeat")
            {
                ReconnectionInterval = ReconnectInterval
            };
            yield return new SseItem<object>(new LogStatusEvent(wasRunning, runtime.ProcessId, lastRunId), "status");

            while (!cancellationToken.IsCancellationRequested)
            {
                LogReadResult read;
                try
                {
                    read = await runtime.WaitForLogReadAsync(lastId, HeartbeatInterval, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Log SSE {ConnectionId} failed while waiting after event {LastId}", connectionId, lastId);
                    break;
                }

                if (read.HasGap && read.OldestAvailableId is long oldest && read.NewestAvailableId is long newest)
                {
                    logger.LogWarning(
                        "Log SSE {ConnectionId} detected a gap: requested {RequestedId}, available {OldestId}-{NewestId}",
                        connectionId, read.RequestedId, oldest, newest);
                    yield return new SseItem<object>(new LogGapEvent(read.RequestedId, oldest, newest), "gap");
                    lastId = oldest - 1;
                }

                foreach (var entry in read.Entries)
                {
                    yield return new SseItem<object>(entry, "message")
                    {
                        EventId = entry.Id.ToString(CultureInfo.InvariantCulture)
                    };
                    lastId = entry.Id;
                }

                var isRunning = runtime.IsRunning;
                var currentRunId = runtime.RunId;
                if (isRunning != wasRunning || currentRunId != lastRunId)
                {
                    yield return new SseItem<object>(new LogStatusEvent(isRunning, runtime.ProcessId, currentRunId), "status");
                    wasRunning = isRunning;
                    lastRunId = currentRunId;
                }

                if (DateTimeOffset.UtcNow >= nextHeartbeat)
                {
                    yield return new SseItem<object>(new LogHeartbeatEvent(DateTimeOffset.UtcNow), "heartbeat");
                    nextHeartbeat = DateTimeOffset.UtcNow.Add(HeartbeatInterval);
                }
            }
        }
        finally
        {
            logger.LogInformation(
                "Log SSE {ConnectionId} closed after event {LastId}; clientAborted={ClientAborted}",
                connectionId, lastId, cancellationToken.IsCancellationRequested);
        }
    }
}
