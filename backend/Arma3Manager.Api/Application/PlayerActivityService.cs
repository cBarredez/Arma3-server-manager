using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Arma3Manager.Api.Configuration;
using Arma3Manager.Api.Contracts;
using Arma3Manager.Api.Infrastructure.Persistence;

namespace Arma3Manager.Api.Application;

/// <summary>Correlates server hooks, RPT output, BattlEye messages, and RCon roster transitions.</summary>
public sealed class PlayerActivityService : BackgroundService
{
    readonly ILogger<PlayerActivityService> logger;
    readonly AppConfig config;
    readonly RuntimeState runtime;
    readonly LogHub logHub;
    readonly BattlEyeRconClient rcon;
    readonly SqliteStore store;
    readonly Channel<object> queue = Channel.CreateBounded<object>(new BoundedChannelOptions(8_192)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    readonly Dictionary<string, RconPlayer> roster = new(StringComparer.OrdinalIgnoreCase);
    readonly object stateGate = new();
    readonly HashSet<string> sources = new(StringComparer.OrdinalIgnoreCase) { "server_hooks", "rpt" };
    bool instrumentationComplete;
    DateTimeOffset? lastEventAt;
    string? lastError;
    long dropped;

    public PlayerActivityService(ILogger<PlayerActivityService> logger, AppConfig config, RuntimeState runtime, LogHub logHub, BattlEyeRconClient rcon, SqliteStore store)
    {
        this.logger = logger;
        this.config = config;
        this.runtime = runtime;
        this.logHub = logHub;
        this.rcon = rcon;
        this.store = store;
        logHub.EntryPushed += OnLogEntry;
        runtime.RunStarted += OnRunStarted;
        runtime.RunEnded += OnRunEnded;
        rcon.ServerMessageReceived += OnRconMessage;
    }

    public PlayerTrackingState State
    {
        get
        {
            lock (stateGate)
            {
                var full = instrumentationComplete && rcon.IsConfigured && rcon.IsConnected && dropped == 0;
                return new(full ? "full" : "partial", sources.Order().ToArray(), lastEventAt, lastError, dropped);
            }
        }
    }

    public void ReportInstrumentation(ServerCfgInstrumentationResult result)
    {
        lock (stateGate)
        {
            instrumentationComplete = result.Complete;
            if (!result.Complete) lastError = result.Errors.Length == 0 ? "Server hooks are incomplete" : string.Join("; ", result.Errors);
            else if (lastError?.StartsWith("Server hooks", StringComparison.Ordinal) == true) lastError = null;
        }
    }

    public void RecordManagerAction(string kind, RconPlayer? player, string? reason, string? response, string actor)
    {
        if (runtime.RunId is not { } runId) return;
        var playerLabel = player?.Name ?? (player is null ? "unknown player" : $"player #{player.Id}");
        var raw = $"Manager {kind}: {playerLabel}; actor={actor}; reason={reason ?? "not provided"}; response={response ?? ""}";
        Enqueue(new PlayerSignal(runId, DateTimeOffset.UtcNow, kind, "manager_action", "authoritative", raw,
            PlayerSignalParser.Fingerprint(runId, kind, player?.Guid, player?.Ip, reason, raw, DateTimeOffset.UtcNow),
            BattlEyeGuid: player?.Guid, RconPlayerId: player?.Id, Name: player?.Name, Ip: player?.Ip,
            ReasonCode: kind == "banned" ? "banned" : "manual_kick", ReasonText: reason, Terminal: true));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await store.PruneHistoryAsync(config.HistoryRetentionDays); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to prune player history during startup");
            lock (stateGate) lastError = exception.Message;
        }
        await Task.WhenAll(ProcessQueueAsync(stoppingToken), MonitorRconAsync(stoppingToken), PruneLoopAsync(stoppingToken));
    }

    async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (await queue.Reader.WaitToReadAsync(ct))
        {
            var items = new List<object>(128);
            while (items.Count < 128 && queue.Reader.TryRead(out var item)) items.Add(item);
            try
            {
                await ProcessBatchAsync(items);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Unable to process player activity batch of {Count} inputs", items.Count);
                lock (stateGate) lastError = $"Activity persistence: {exception.Message}";
            }
        }
    }

    async Task ProcessBatchAsync(IReadOnlyList<object> items)
    {
        var signals = new List<PlayerSignal>(items.Count);
        foreach (var item in items)
        {
            switch (item)
            {
                case ServerRunStarted started:
                    await FlushSignalsAsync(signals);
                    await store.StartServerSessionAsync(started);
                    break;
                case ServerRunEnded ended:
                    await FlushSignalsAsync(signals);
                    await store.EndServerSessionAsync(ended);
                    roster.Clear();
                    break;
                case LogEntry entry when PlayerSignalParser.TryParse(entry, out var signal):
                    signals.Add(signal!);
                    break;
                case PlayerSignal signal:
                    signals.Add(signal);
                    break;
            }
        }
        await FlushSignalsAsync(signals);
    }

    async Task FlushSignalsAsync(List<PlayerSignal> signals)
    {
        if (signals.Count == 0) return;
        if (await store.ApplyPlayerSignalsAsync(signals) > 0)
        {
            lock (stateGate)
            {
                foreach (var source in signals.Select(signal => signal.Source)) sources.Add(source);
                lastEventAt = signals.Max(signal => signal.OccurredAt);
                if (lastError?.StartsWith("Activity persistence:", StringComparison.Ordinal) == true) lastError = null;
            }
        }
        signals.Clear();
    }

    async Task MonitorRconAsync(CancellationToken ct)
    {
        var failures = 0;
        int[] delays = [2, 5, 10, 30];
        while (!ct.IsCancellationRequested)
        {
            if (!runtime.IsRunning || runtime.RunId is null)
            {
                failures = 0;
                roster.Clear();
                if (rcon.IsConnected) await rcon.DisconnectAsync();
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }
            if (!rcon.IsConfigured)
            {
                lock (stateGate) lastError = "BattlEye RCon is not configured";
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            try
            {
                var players = (await rcon.GetPlayersAsync(ct)).Where(player => !IsHeadless(player)).ToArray();
                ReconcileRoster(runtime.RunId, players);
                failures = 0;
                lock (stateGate)
                {
                    sources.Add("rcon_roster");
                    if (lastError?.Contains("RCon", StringComparison.OrdinalIgnoreCase) == true) lastError = null;
                }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                failures++;
                lock (stateGate) lastError = $"RCon: {exception.Message}";
                await Task.Delay(TimeSpan.FromSeconds(delays[Math.Min(failures - 1, delays.Length - 1)]), ct);
            }
        }
    }

    void ReconcileRoster(string runId, IReadOnlyList<RconPlayer> players)
    {
        var current = players.ToDictionary(PlayerKey, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, player) in current)
        {
            if (roster.ContainsKey(key)) continue;
            var at = DateTimeOffset.UtcNow;
            var raw = $"RCon player verified: #{player.Id} {player.Name} {player.Ip} {player.Guid}";
            Enqueue(new PlayerSignal(runId, at, "rcon_seen", "rcon_roster", "authoritative", raw,
                PlayerSignalParser.Fingerprint(runId, "rcon_seen", player.Guid, player.Ip, player.Name, raw, at),
                BattlEyeGuid: NullGuid(player.Guid), RconPlayerId: player.Id, Name: player.Name, Ip: player.Ip, Admitted: true));
        }
        foreach (var (key, player) in roster)
        {
            if (current.ContainsKey(key)) continue;
            var at = DateTimeOffset.UtcNow;
            var raw = $"RCon player left roster: #{player.Id} {player.Name} {player.Ip} {player.Guid}";
            Enqueue(new PlayerSignal(runId, at, "disconnected", "rcon_roster", "authoritative", raw,
                PlayerSignalParser.Fingerprint(runId, "disconnected", player.Guid, player.Ip, player.Name, raw, at),
                BattlEyeGuid: NullGuid(player.Guid), RconPlayerId: player.Id, Name: player.Name, Ip: player.Ip, Terminal: true));
        }
        roster.Clear();
        foreach (var pair in current) roster[pair.Key] = pair.Value;
    }

    void OnLogEntry(LogEntry entry)
    {
        if (entry.RunId is null || !PlayerSignalParser.IsCandidate(entry.Data)) return;
        Enqueue(entry);
    }

    void OnRunStarted(ServerRunStarted run) => Enqueue(run);
    void OnRunEnded(ServerRunEnded run) => Enqueue(run);

    void OnRconMessage(string text)
    {
        if (runtime.RunId is not { } runId) return;
        lock (stateGate) sources.Add("battleye_message");
        OnLogEntry(new("rcon", text, DateTimeOffset.UtcNow, Source: "battleye_message", RunId: runId));
    }

    void Enqueue(object item)
    {
        if (queue.Writer.TryWrite(item)) return;
        var count = Interlocked.Increment(ref dropped);
        lock (stateGate) lastError = $"Player tracking queue overflowed; {count} candidate events were dropped";
        logger.LogError("Player tracking queue overflowed; dropped input {DroppedCount}", count);
    }

    async Task PruneLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (await timer.WaitForNextTickAsync(ct))
            try { await store.PruneHistoryAsync(config.HistoryRetentionDays); }
            catch (Exception exception) { logger.LogWarning(exception, "Unable to prune player history"); }
    }

    static bool IsHeadless(RconPlayer player) => player.Ip.StartsWith("127.", StringComparison.Ordinal) ||
        player.Name.StartsWith("HC", StringComparison.OrdinalIgnoreCase) || player.Name.Contains("headless", StringComparison.OrdinalIgnoreCase);
    static string PlayerKey(RconPlayer player) => NullGuid(player.Guid) is { } guid ? $"g:{guid}" : $"i:{player.Ip}|{player.Name}";
    static string? NullGuid(string? guid) => string.IsNullOrWhiteSpace(guid) || guid is "-" or "?" ? null : guid;

    public override void Dispose()
    {
        logHub.EntryPushed -= OnLogEntry;
        runtime.RunStarted -= OnRunStarted;
        runtime.RunEnded -= OnRunEnded;
        rcon.ServerMessageReceived -= OnRconMessage;
        base.Dispose();
    }
}

public static class PlayerSignalParser
{
    static readonly Regex Marker = new(@"A3MGR_PLAYER_V1\|(?<event>[A-Z_]+)\|(?<payload>.*)$", RegexOptions.Compiled);
    static readonly Regex RconConnected = new(@"Player\s+#?(?<id>\d+)?\s*(?<name>.*?)\s*\((?<guid>[0-9a-f]{16,})\).*?connected", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex RconKicked = new(@"Player\s+#?(?<id>\d+)?\s*(?<name>.*?)\s*\((?<guid>[0-9a-f]{16,})\).*?(?:kicked|banned)(?:.*?:\s*(?<reason>.*))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex NaturalConnection = new(@"\bPlayer\s+(?<name>[^\r\n]+?)\s+(?<event>connected|disconnected)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly string[] CandidateTerms = ["A3MGR_PLAYER_V1", "player", "battleye", "steam check", "signature", "unsigned", "missing addon", "wrong password", "session locked", "kicked", "banned", "disconnected"];

    public static bool IsCandidate(string text) => CandidateTerms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    public static bool TryParse(LogEntry entry, out PlayerSignal? signal)
    {
        signal = null;
        if (entry.RunId is null) return false;
        var marker = Marker.Match(entry.Data);
        if (marker.Success)
        {
            signal = ParseMarker(entry, marker.Groups["event"].Value, marker.Groups["payload"].Value);
            return signal is not null;
        }

        var connected = RconConnected.Match(entry.Data);
        if (connected.Success)
        {
            signal = Build(entry, "rcon_seen", name: connected.Groups["name"].Value.Trim(), guid: connected.Groups["guid"].Value,
                rconId: ParseInt(connected.Groups["id"].Value), admitted: true, confidence: "inferred");
            return true;
        }
        var kicked = RconKicked.Match(entry.Data);
        if (kicked.Success)
        {
            var reason = EmptyToNull(kicked.Groups["reason"].Value);
            signal = Build(entry, entry.Data.Contains("banned", StringComparison.OrdinalIgnoreCase) ? "banned" : "kicked",
                name: kicked.Groups["name"].Value.Trim(), guid: kicked.Groups["guid"].Value,
                rconId: ParseInt(kicked.Groups["id"].Value), reasonCode: ClassifyReason(reason), reasonText: reason,
                terminal: true, confidence: "inferred");
            return true;
        }
        var natural = NaturalConnection.Match(entry.Data);
        if (natural.Success)
        {
            var kind = natural.Groups["event"].Value.Equals("connected", StringComparison.OrdinalIgnoreCase) ? "connected" : "disconnected";
            signal = Build(entry, kind, name: natural.Groups["name"].Value.Trim(), admitted: kind == "connected", terminal: kind == "disconnected", confidence: "inferred");
            return true;
        }
        return false;
    }

    static PlayerSignal? ParseMarker(LogEntry entry, string eventName, string payload)
    {
        var values = ParseValues(payload);
        var networkId = values.ElementAtOrDefault(0);
        var kind = eventName switch
        {
            "CONNECTED" => "connected",
            "DISCONNECTED" => "disconnected",
            "KICKED" => "kicked",
            "DUPLICATE_ID" => "duplicate_id",
            "UNSIGNED_DATA" => "unsigned_data",
            "HACKED_DATA" => "hacked_data",
            "DIFFERENT_DATA" => "different_data",
            _ => "unknown"
        };
        var typeNumber = eventName == "KICKED" ? ParseInt(values.ElementAtOrDefault(1)) : null;
        var reason = eventName == "KICKED" ? values.ElementAtOrDefault(2) : values.ElementAtOrDefault(1);
        var reasonCode = eventName switch
        {
            "DUPLICATE_ID" => "duplicate_id",
            "UNSIGNED_DATA" => "unsigned_data",
            "HACKED_DATA" => "hacked_data",
            "DIFFERENT_DATA" => "different_data",
            "KICKED" => KickReason(typeNumber, reason),
            _ => null
        };
        return Build(entry, kind, networkId: networkId, reasonCode: reasonCode, reasonText: reason,
            admitted: eventName == "CONNECTED", terminal: eventName is "DISCONNECTED" or "KICKED", confidence: "authoritative");
    }

    static PlayerSignal Build(LogEntry entry, string kind, string? networkId = null, string? guid = null, int? rconId = null,
        string? name = null, string? ip = null, string? reasonCode = null, string? reasonText = null,
        bool admitted = false, bool terminal = false, string confidence = "unknown")
    {
        var at = entry.Ts;
        return new(entry.RunId!, at, kind, entry.Source, confidence, entry.Data,
            Fingerprint(entry.RunId!, kind, guid ?? networkId, ip, reasonText, entry.Data, at),
            NetworkId: networkId, BattlEyeGuid: guid, RconPlayerId: rconId, Name: name, Ip: ip,
            ReasonCode: reasonCode, ReasonText: reasonText, Admitted: admitted, Terminal: terminal);
    }

    public static string Fingerprint(string runId, string kind, string? identity, string? ip, string? reason, string raw, DateTimeOffset at)
    {
        var normalized = Regex.Replace(raw.Trim(), @"^(?:\d{1,2}:\d{2}:\d{2}\s*)+", "");
        var marker = normalized.IndexOf(ServerCfgInstrumentation.Marker, StringComparison.Ordinal);
        if (marker >= 0) normalized = normalized[marker..];
        normalized = normalized.Trim().Trim('"').ToLowerInvariant();
        var bucket = at.ToUnixTimeSeconds() / 5;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{runId}|{kind}|{identity}|{ip}|{reason}|{normalized}|{bucket}"));
        return Convert.ToHexString(bytes);
    }

    static string? KickReason(int? type, string? reason) => type switch
    {
        0 => "network_timeout", 1 => "disconnected", 2 => "manual_kick", 3 => "banned",
        4 => "missing_addon", 5 => "bad_cd_key", 6 => "cd_key_in_use", 7 => "session_locked",
        8 => "battleye", 9 => "steam_check", 10 => "dlc_content", 11 => "network_timeout", 12 => "script",
        _ => ClassifyReason(reason)
    };

    static string ClassifyReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "unknown";
        if (reason.Contains("addon", StringComparison.OrdinalIgnoreCase)) return "missing_addon";
        if (reason.Contains("signature", StringComparison.OrdinalIgnoreCase) || reason.Contains("unsigned", StringComparison.OrdinalIgnoreCase)) return "unsigned_data";
        if (reason.Contains("battleye", StringComparison.OrdinalIgnoreCase)) return "battleye";
        if (reason.Contains("steam", StringComparison.OrdinalIgnoreCase)) return "steam_check";
        if (reason.Contains("password", StringComparison.OrdinalIgnoreCase)) return "wrong_password";
        if (reason.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return "network_timeout";
        return "unknown";
    }

    static List<string> ParseValues(string payload)
    {
        var text = payload.Trim().Trim('"').Trim().Trim('[', ']');
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"') { current.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (character == ',' && !quoted) { values.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(character);
        }
        if (current.Length > 0 || text.EndsWith(',')) values.Add(current.ToString().Trim());
        return values.Select(value => value.Trim().Trim('"')).ToList();
    }

    static int? ParseInt(string? value) => int.TryParse(value, out var parsed) ? parsed : null;
    static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
