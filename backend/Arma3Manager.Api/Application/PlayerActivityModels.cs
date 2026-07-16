using Arma3Manager.Api.Contracts;

namespace Arma3Manager.Api.Application;

public sealed record ServerRunStarted(string RunId, DateTimeOffset StartedAt, int Pid);
public sealed record ServerRunEnded(string RunId, DateTimeOffset EndedAt, int? ExitCode, string Reason);
public sealed record ServerProcessIdentity(int Pid, long StartedAtUtcTicks, string BinaryPath);

public sealed record PlayerSignal(
    string RunId,
    DateTimeOffset OccurredAt,
    string Kind,
    string Source,
    string Confidence,
    string RawText,
    string DedupeKey,
    string? NetworkId = null,
    string? SteamUid = null,
    string? BattlEyeGuid = null,
    int? RconPlayerId = null,
    string? Name = null,
    string? Ip = null,
    string? ReasonCode = null,
    string? ReasonText = null,
    bool Admitted = false,
    bool Terminal = false);

public static class PlayerOutcomes
{
    public const string Pending = "pending";
    public const string Successful = "successful";
    public const string Rejected = "rejected";
    public const string Removed = "removed";
    public const string Unknown = "unknown";

    public static string Resolve(PlayerSignal signal, PlayerConnectionRecord? existing)
    {
        if (signal.ReasonCode is "missing_addon" or "bad_cd_key" or "cd_key_in_use" or "session_locked" or
            "steam_check" or "dlc_content" or "unsigned_data" or "hacked_data" or "different_data" or "duplicate_id")
            return Rejected;
        if (signal.Kind is "rejected" or "unsigned_data" or "hacked_data" or "different_data" or "duplicate_id")
            return Rejected;
        if (signal.Kind is "kicked" or "banned")
            return existing?.AdmittedAt is not null ? Removed : Rejected;
        if (signal.Admitted || signal.Kind is "connected" or "rcon_seen") return Successful;
        if (signal.Kind == "disconnected") return existing?.Outcome == Successful ? Successful : Unknown;
        return existing?.Outcome ?? Pending;
    }
}
