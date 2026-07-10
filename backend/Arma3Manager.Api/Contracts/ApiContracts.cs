namespace Arma3Manager.Api.Contracts;

/// <summary>Installed or discovered Arma 3 mod.</summary>
public sealed record Mod(string Id, string Name, string Path, bool Active, string? WorkshopId);
public sealed record SteamAuth(string Username, DateTimeOffset UpdatedAt);
public sealed record ModlistState(string? ActiveModlistId, List<Modlist> Lists);
public sealed record Modlist(string Id, string Name, List<PresetMod> Mods, DateTimeOffset CreatedAt);
public sealed record PresetMod(string Name, string WorkshopId);
public sealed record LogEntry(string Type, string Data, DateTimeOffset Ts);
public sealed record FileItem(string Name, string Path, bool IsDir, long Size, DateTime Modified);
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
public sealed record SteamLoginRequest(string Username, string Password);
public sealed record SteamInputRequest(string Input);
public sealed record AccountUpdateRequest(string Username, string CurrentPassword, string NewPassword);
public sealed record PanelAuth(string Username, string PasswordSalt, string PasswordHash);
