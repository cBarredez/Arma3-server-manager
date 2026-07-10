using Arma3Manager.Api.Configuration;

namespace Arma3Manager.Api.Domain;

/// <summary>Resolved filesystem locations used by the game server and manager.</summary>
public sealed record ServerPaths(string Arma3Dir, string Arma3Bin, string SteamCmd, string ConfigDir, string ProfilesDir, string MissionsDir, string KeysDir, string WorkshopDir, string ModsRoot)
{
    public static async Task<ServerPaths> DetectAsync(AppConfig cfg)
    {
        var steamInDir = Path.Combine(cfg.Arma3Dir, "steamcmd", "steamcmd.sh");
        var steamGlob = Path.Combine(cfg.SteamCmdDir, "steamcmd.sh");
        var steam = File.Exists(steamInDir) ? steamInDir : steamGlob;
        var bin = File.Exists(Path.Combine(cfg.Arma3Dir, "arma3server_x64"))
            ? Path.Combine(cfg.Arma3Dir, "arma3server_x64")
            : Path.Combine(cfg.Arma3Dir, "arma3server");
        var config = File.Exists(Path.Combine(cfg.Arma3Dir, "config", "server.cfg")) ? Path.Combine(cfg.Arma3Dir, "config") : cfg.Arma3Dir;
        var profiles = Directory.Exists(Path.Combine(cfg.Arma3Dir, "serverprofile")) ? Path.Combine(cfg.Arma3Dir, "serverprofile") : Path.Combine(cfg.Arma3Dir, "profiles");
        var missions = Directory.Exists(Path.Combine(cfg.Arma3Dir, "mpmissions")) ? Path.Combine(cfg.Arma3Dir, "mpmissions") : Path.Combine(cfg.Arma3Dir, "missions");
        foreach (var directory in new[] { cfg.Arma3Dir, config, profiles, missions, Path.Combine(cfg.Arma3Dir, "keys") }) Directory.CreateDirectory(directory);
        await SeedDefaultConfigAsync(config);
        return new(cfg.Arma3Dir, bin, steam, config, profiles, missions, Path.Combine(cfg.Arma3Dir, "keys"), Path.Combine(cfg.Arma3Dir, "steamapps", "workshop", "content", "107410"), cfg.Arma3Dir);
    }

    static async Task SeedDefaultConfigAsync(string configDir)
    {
        const string defaults = "/defaults/config";
        if (!Directory.Exists(defaults)) return;
        foreach (var file in new[] { "server.cfg", "basic.cfg" })
        {
            var destination = Path.Combine(configDir, file);
            var source = Path.Combine(defaults, file);
            if (File.Exists(destination) || !File.Exists(source)) continue;
            await using var input = File.OpenRead(source);
            await using var output = File.Create(destination);
            await input.CopyToAsync(output);
        }
    }
}

/// <summary>Mutable server startup settings persisted in SQLite.</summary>
public sealed record StartupSettings(string ServerBinary, string Ip, int Port, string ProfilesDir, string ServerCfg, string BasicCfg, string ExtraParams, int? MaxPlayers, string ServerPassword, bool AutomaticUpdates, bool DownloadCreatorDlcs, bool LowerCaseMods, bool ValidateServerFiles, bool DisableBattleEye, string ServerMods, string OptionalClientMods, int[] ExtraPorts, int HeadlessClients, string SteamCmdFlags)
{
    public StartupSettings Normalized(ServerPaths paths, AppConfig cfg) => this with
    {
        ServerBinary = "arma3server_x64",
        Ip = string.IsNullOrWhiteSpace(Ip) ? "0.0.0.0" : Ip,
        Port = Port is < 1 or > 65535 ? cfg.ServerPort : Port,
        ProfilesDir = string.IsNullOrWhiteSpace(ProfilesDir) ? paths.ProfilesDir : ProfilesDir,
        ServerCfg = string.IsNullOrWhiteSpace(ServerCfg) ? Path.Combine(paths.ConfigDir, "server.cfg") : ServerCfg,
        BasicCfg = string.IsNullOrWhiteSpace(BasicCfg) ? Path.Combine(paths.ConfigDir, "basic.cfg") : BasicCfg,
        MaxPlayers = MaxPlayers ?? cfg.ServerMaxPlayers,
        ExtraPorts = ExtraPorts.Where(port => port is > 0 and < 65536).Distinct().ToArray(),
        HeadlessClients = Math.Clamp(HeadlessClients, 0, 5)
    };

    public static StartupSettings Default(ServerPaths paths, AppConfig cfg) => new("arma3server_x64", "0.0.0.0", cfg.ServerPort, paths.ProfilesDir, Path.Combine(paths.ConfigDir, "server.cfg"), Path.Combine(paths.ConfigDir, "basic.cfg"), "-autoInit -preload -limitFPS=120 -bandwidthAlg=2 -maxFileCacheSize -noSound", cfg.ServerMaxPlayers, "", false, false, false, false, false, "", "", [], 0, "");
}
