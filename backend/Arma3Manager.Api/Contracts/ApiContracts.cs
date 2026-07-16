namespace Arma3Manager.Api.Contracts;

/// <summary>Installed or discovered Arma 3 mod.</summary>
public sealed record Mod(string Id, string Name, string Path, bool Active, string? WorkshopId);
public sealed record SteamAuth(string Username, DateTimeOffset UpdatedAt);
public sealed record ModlistState(string? ActiveModlistId, List<Modlist> Lists);
public sealed record Modlist(string Id, string Name, List<PresetMod> Mods, DateTimeOffset CreatedAt);
public sealed record PresetMod(string Name, string WorkshopId, bool Installed = false);
public sealed record LogEntry(
    string Type,
    string Data,
    DateTimeOffset Ts,
    long Id = 0,
    string Source = "manager",
    string? RunId = null);
public sealed record FileItem(string Name, string Path, bool IsDir, long Size, DateTime Modified, DateTime? Created = null);
public sealed record FileIndexRow(string Path, string Parent, string Name, bool IsDir, long Size, DateTime Created, DateTime MTime, long ScanGen);
public sealed record SavedPresetFile(string Name, string Path, long Size, DateTime Modified);
public sealed record LoginRequest(string Username, string Password);
public sealed record ModUpdate(bool Active);
public sealed record InstallModRequest(string WorkshopId, string? Name);
public sealed record InstallBatchRequest(List<PresetMod> Mods);
public sealed record ModlistSaveRequest(string Name, List<PresetMod> Mods, bool Activate);
public sealed record PresetFileLoadRequest(string Path);
public sealed record CreatorDlcUpdate(bool Active);
public sealed record CreatorDlc(string Id, string Name, string Folder, string Path, bool Available, bool Active);
public sealed record FileWriteRequest(string Path, string Content);
public sealed record FileRenameRequest(string Path, string NewName);
public sealed record ConfigWriteRequest(string File, string Content);
public sealed record MissionSelectionRequest(string Template);
public sealed record MissionEntry(string Template, string Name, bool Packed, long Size, DateTime Modified);
public sealed record SteamLoginRequest(string Username, string Password);
public sealed record SteamInputRequest(string Input);
public sealed record AccountUpdateRequest(string Username, string CurrentPassword, string NewPassword);
public sealed record FactoryResetRequest(string CurrentPassword, string Confirmation);
public sealed record RestartAppRequest(string CurrentPassword);
public sealed record MetricsSample(
    string RunId,
    DateTimeOffset SampledAt,
    double? CpuPercent,
    double CoresCapacity,
    long MemoryUsedBytes,
    double MemoryPercent,
    double? CpuUsagePercent = null,
    int? ActivePlayers = null,
    int? ActiveHeadlessClients = null)
{
    public double? CpuCoresUsed => CpuUsagePercent / 100d;
}

public sealed record MetricsSessionSummary(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int SampleCount,
    double? AvgCpuPercent,
    double? PeakCpuPercent,
    double CoresCapacity,
    double? AvgMemoryPercent,
    double? PeakMemoryPercent,
    double? AvgCpuUsagePercent = null,
    double? PeakCpuUsagePercent = null,
    double? AvgCpuCoresUsed = null,
    double? PeakCpuCoresUsed = null,
    int? PeakActivePlayers = null,
    int? PeakActiveHeadlessClients = null);

public sealed record MetricsSessionDetail(MetricsSessionSummary Session, IReadOnlyList<MetricsSample> Samples);
public sealed record ServerLifecycleStatus(
    string Phase,
    bool Running,
    bool Busy,
    string? Stage,
    string? OperationId,
    DateTimeOffset Since,
    string? LastError,
    bool Managed,
    int? Pid,
    string? RunId,
    int[] ConflictingPids);
public sealed record PanelAuth(string Username, string PasswordSalt, string PasswordHash);
public sealed record RconPlayer(int Id, string Guid, string Name, string Ip, int Ping, bool Lobby, bool Verified);
public sealed record RconCommandRequest(string Command);
public sealed record RconSayRequest(string Message);
public sealed record RconKickRequest(int PlayerId, string? Reason);
public sealed record RconBanRequest(int PlayerId, int Minutes, string? Reason);

public sealed record ServerSessionSummary(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int? Pid,
    int? ExitCode,
    string EndReason,
    int Successful,
    int Rejected,
    int Removed,
    int Pending,
    int UniquePlayers,
    int SampleCount = 0,
    double? AvgCpuUsagePercent = null,
    double? PeakCpuUsagePercent = null,
    double? AvgCpuCoresUsed = null,
    double? PeakCpuCoresUsed = null,
    double? AvgMemoryPercent = null,
    double? PeakMemoryPercent = null,
    int? PeakActivePlayers = null,
    int? PeakActiveHeadlessClients = null);

public sealed record PlayerConnectionRecord(
    long Id,
    string RunId,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset? AdmittedAt,
    DateTimeOffset? EndedAt,
    string Outcome,
    bool Active,
    string? NetworkId,
    string? SteamUid,
    string? BattlEyeGuid,
    int? RconPlayerId,
    string? Name,
    string? Ip,
    string? ReasonCode,
    string? ReasonText,
    string Source,
    string Confidence);

public sealed record PlayerEventRecord(
    long Id,
    long? ConnectionId,
    string RunId,
    DateTimeOffset OccurredAt,
    string Kind,
    string? ReasonCode,
    string? ReasonText,
    string Source,
    string Confidence,
    string RawText);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor);

public sealed record PlayerTrackingState(
    string Mode,
    string[] Sources,
    DateTimeOffset? LastEventAt,
    string? LastError,
    long DroppedCandidates = 0,
    string Profile = "log_identity",
    string[]? AvailableFields = null);
