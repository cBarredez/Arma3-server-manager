using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Arma3Manager.Api.Contracts;

namespace Arma3Manager.Api.Application;

public sealed record LogGapEvent(long RequestedId, long OldestAvailableId, long NewestAvailableId);
public sealed record LogHeartbeatEvent(DateTimeOffset Ts);
public sealed record LogBatchEvent(IReadOnlyList<LogEntry> Entries);

/// <summary>Produces resumable, typed server-sent events from bounded per-client subscriptions.</summary>
public sealed class LogStreamService
{
    readonly RuntimeState runtime;
    readonly ServerLifecycleCoordinator? lifecycle;
    readonly ILogger<LogStreamService> logger;

    public LogStreamService(RuntimeState runtime, ILogger<LogStreamService> logger) : this(runtime, null, logger) { }
    public LogStreamService(RuntimeState runtime, ServerLifecycleCoordinator? lifecycle, ILogger<LogStreamService> logger)
    {
        this.runtime = runtime;
        this.lifecycle = lifecycle;
        this.logger = logger;
    }

    public const int MaxBatchEntries = 100;
    public const int MaxBatchBytes = 256 * 1024;
    public static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(50);
    static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(3);

    public async IAsyncEnumerable<SseItem<object>> Stream(
        long afterId,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        bool batch = false)
    {
        var connectionId = Guid.NewGuid().ToString("N")[..8];
        var lastId = Math.Max(0, afterId);
        var previousStatus = await CurrentStatusAsync();
        var nextHeartbeat = DateTimeOffset.UtcNow.Add(HeartbeatInterval);
        await using var subscription = runtime.SubscribeToLogs(lastId);
        logger.LogInformation("Log SSE {ConnectionId} opened after event {LastId}; batched={Batched}", connectionId, lastId, batch);

        try
        {
            yield return new SseItem<object>(new LogHeartbeatEvent(DateTimeOffset.UtcNow), "heartbeat")
            {
                ReconnectionInterval = ReconnectInterval
            };
            yield return new SseItem<object>(previousStatus, "status");

            var initial = subscription.Initial;
            if (initial.HasGap && initial.OldestAvailableId is long initialOldest && initial.NewestAvailableId is long initialNewest)
            {
                yield return Gap(connectionId, initial.RequestedId, initialOldest, initialNewest);
                lastId = initialOldest - 1;
            }
            foreach (var item in LogItems(initial.Entries, batch))
            {
                yield return item;
                lastId = ItemLastId(item);
            }

            LogEntry? carry = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                LogEntry? first = carry;
                carry = null;
                if (first is null)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    wait.CancelAfter(HeartbeatInterval);
                    try { first = await subscription.Reader.ReadAsync(wait.Token); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                    catch (ChannelClosedException) { break; }
                }

                if (first is not null && first.Id > lastId)
                {
                    var expectedId = lastId > 0 ? lastId + 1 : subscription.NextExpectedId;
                    if (first.Id > expectedId)
                    {
                        var recoveryCursor = lastId > 0 ? lastId : expectedId - 1;
                        var recovered = runtime.ReadLogsAfter(recoveryCursor);
                        if (recovered.HasGap && recovered.OldestAvailableId is long oldest && recovered.NewestAvailableId is long newest)
                        {
                            yield return Gap(connectionId, recovered.RequestedId, oldest, newest);
                            lastId = oldest - 1;
                        }
                        foreach (var item in LogItems(recovered.Entries, batch))
                        {
                            yield return item;
                            lastId = ItemLastId(item);
                        }
                    }
                    else
                    {
                        var pending = new List<LogEntry>(batch ? MaxBatchEntries : 1) { first };
                        var bytes = EntryBytes(first);
                        if (batch)
                        {
                            try { await Task.Delay(BatchWindow, cancellationToken); }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                            while (pending.Count < MaxBatchEntries && subscription.Reader.TryRead(out var candidate))
                            {
                                if (candidate.Id <= pending[^1].Id) continue;
                                if (candidate.Id > pending[^1].Id + 1 || bytes + EntryBytes(candidate) > MaxBatchBytes)
                                {
                                    carry = candidate;
                                    break;
                                }
                                pending.Add(candidate);
                                bytes += EntryBytes(candidate);
                            }
                        }
                        foreach (var item in LogItems(pending, batch))
                        {
                            yield return item;
                            lastId = ItemLastId(item);
                        }
                    }
                }

                var currentStatus = await CurrentStatusAsync();
                if (currentStatus.Phase != previousStatus.Phase || currentStatus.Stage != previousStatus.Stage ||
                    currentStatus.Pid != previousStatus.Pid || currentStatus.OperationId != previousStatus.OperationId)
                {
                    yield return new SseItem<object>(currentStatus, "status");
                    previousStatus = currentStatus;
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

    SseItem<object> Gap(string connectionId, long requested, long oldest, long newest)
    {
        logger.LogWarning(
            "Log SSE {ConnectionId} detected a gap: requested {RequestedId}, available {OldestId}-{NewestId}",
            connectionId, requested, oldest, newest);
        return new SseItem<object>(new LogGapEvent(requested, oldest, newest), "gap");
    }

    static IEnumerable<SseItem<object>> LogItems(IReadOnlyList<LogEntry> entries, bool batch)
    {
        if (!batch)
        {
            foreach (var entry in entries)
                yield return new SseItem<object>(entry, "message") { EventId = entry.Id.ToString(CultureInfo.InvariantCulture) };
            yield break;
        }

        var offset = 0;
        while (offset < entries.Count)
        {
            var chunk = new List<LogEntry>(Math.Min(MaxBatchEntries, entries.Count - offset));
            var bytes = 0;
            while (offset < entries.Count && chunk.Count < MaxBatchEntries)
            {
                var entry = entries[offset];
                var entryBytes = EntryBytes(entry);
                if (chunk.Count > 0 && bytes + entryBytes > MaxBatchBytes) break;
                chunk.Add(entry);
                bytes += entryBytes;
                offset++;
            }
            yield return new SseItem<object>(new LogBatchEvent(chunk), "logs")
            {
                EventId = chunk[^1].Id.ToString(CultureInfo.InvariantCulture)
            };
        }
    }

    static long ItemLastId(SseItem<object> item) =>
        item.Data is LogEntry entry ? entry.Id : ((LogBatchEvent)item.Data).Entries[^1].Id;

    static int EntryBytes(LogEntry entry) => Encoding.UTF8.GetByteCount(entry.Data) + 256;

    async Task<ServerLifecycleStatus> CurrentStatusAsync()
    {
        if (lifecycle is not null) return await lifecycle.GetStatusAsync();
        var running = runtime.IsRunning;
        return new(running ? "running" : "stopped", running, false, null, null, DateTimeOffset.UtcNow,
            null, running, runtime.ProcessId, runtime.RunId, []);
    }
}
