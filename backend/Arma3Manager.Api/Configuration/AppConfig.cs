using System.Globalization;
using System.Text;

namespace Arma3Manager.Api.Configuration;

/// <summary>Immutable bootstrap configuration loaded from TOML files.</summary>
public sealed record AppConfig(
    int WebPort,
    string WebBindIp,
    string WebUsername,
    string WebPassword,
    string SessionSecret,
    string Arma3Dir,
    string SteamCmdDir,
    string SteamUser,
    string SteamPass,
    int ServerPort,
    int ServerQueryPort,
    int BattleEyePort,
    int VonPort,
    int RconPort,
    string RconPassword,
    int ServerMaxPlayers,
    string ServerMemoryLimit,
    string NetworkMode,
    string BaseUrl,
    string PublicJoinHost,
    string[] CreatorDlcAppIds,
    HashSet<string> SteamOwnerIds,
    string? FrontendOrigin,
    string TimeZone,
    bool MockServer,
    bool MockSteamCmd)
{
    /// <summary>Loads public configuration and overlays an optional private secrets file.</summary>
    public static AppConfig Load(string contentRoot)
    {
        var configuredPath = Environment.GetCommandLineArgs()
            .FirstOrDefault(arg => arg.StartsWith("--config=", StringComparison.Ordinal))?
            .Split('=', 2)[1];
        var publicPath = configuredPath is null
            ? FindProjectFile(contentRoot, "config/manager.toml")
            : ResolvePath(contentRoot, configuredPath);
        var configDirectory = Path.GetDirectoryName(publicPath)!;
        var localSecretsPath = Path.Combine(configDirectory, "manager.secrets.toml");
        var podmanSecretsPath = "/run/secrets/manager.secrets.toml";
        var secretsPath = File.Exists(localSecretsPath) ? localSecretsPath : podmanSecretsPath;
        return LoadFiles(publicPath, File.Exists(secretsPath) ? secretsPath : null);
    }

    /// <summary>Loads explicitly selected TOML files; intended for tests and deployment validation.</summary>
    public static AppConfig LoadFiles(string publicPath, string? secretsPath = null)
    {
        var values = SimpleToml.Read(publicPath);
        if (secretsPath is not null && File.Exists(secretsPath)) SimpleToml.Overlay(values, SimpleToml.Read(secretsPath));

        var config = new AppConfig(
            Int(values, "web.port", 8080),
            Text(values, "web.bind_ip", "127.0.0.1"),
            Text(values, "web.username", "admin"),
            Text(values, "web.password", "change-this-panel-password"),
            Text(values, "web.session_secret", ""),
            Text(values, "server.arma3_dir", "/arma3"),
            Text(values, "server.steamcmd_dir", "/steamcmd"),
            Text(values, "steam.user", "anonymous"),
            Text(values, "steam.password", ""),
            Int(values, "server.port", 2302),
            Int(values, "server.query_port", 2303),
            Int(values, "server.battleye_port", 2304),
            Int(values, "server.von_port", 2305),
            Int(values, "server.rcon_port", 2306),
            Text(values, "server.rcon_password", ""),
            Int(values, "server.max_players", 40),
            Text(values, "server.memory_limit", "14g"),
            Text(values, "server.network_mode", "bridge"),
            Text(values, "web.base_url", ""),
            Text(values, "web.public_join_host", ""),
            Array(values, "steam.creator_dlc_app_ids").Where(IsNumeric).Distinct().ToArray(),
            Array(values, "steam.owner_ids").Where(IsNumeric).ToHashSet(StringComparer.Ordinal),
            EmptyToNull(Text(values, "web.frontend_origin", "")),
            Text(values, "runtime.timezone", "UTC"),
            Bool(values, "runtime.mock_server", false),
            Bool(values, "runtime.mock_steamcmd", false));

        return config.Validate();
    }

    AppConfig Validate()
    {
        foreach (var (name, port) in new[]
        {
            ("web.port", WebPort), ("server.port", ServerPort),
            ("server.query_port", ServerQueryPort), ("server.battleye_port", BattleEyePort),
            ("server.von_port", VonPort), ("server.rcon_port", RconPort)
        })
            if (port is < 1 or > 65535) throw new InvalidDataException($"{name} must be between 1 and 65535");
        int[] gamePorts = [ServerPort, ServerQueryPort, BattleEyePort, VonPort, RconPort, WebPort];
        if (gamePorts.Distinct().Count() != gamePorts.Length)
            throw new InvalidDataException("server.port, server.query_port, server.battleye_port, server.von_port, server.rcon_port and web.port must all be different");
        if (ServerMaxPlayers is < 1 or > 500) throw new InvalidDataException("server.max_players must be between 1 and 500");
        if (string.IsNullOrWhiteSpace(Arma3Dir) || !Path.IsPathRooted(Arma3Dir))
            throw new InvalidDataException("server.arma3_dir must be an absolute path");
        if (NetworkMode is not ("bridge" or "host"))
            throw new InvalidDataException("server.network_mode must be 'bridge' or 'host'");
        if (string.IsNullOrWhiteSpace(WebPassword) || WebPassword == "change-this-panel-password")
            throw new InvalidDataException("web.password must be defined in manager.secrets.toml");
        if (SessionSecret.Length < 32)
            throw new InvalidDataException("web.session_secret must contain at least 32 characters");
        return this;
    }

    static string ResolvePath(string root, string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    static string FindProjectFile(string root, string relativePath)
    {
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        return Path.Combine(root, relativePath);
    }
    static string Text(Dictionary<string, object> values, string key, string fallback) => values.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback : fallback;
    static int Int(Dictionary<string, object> values, string key, int fallback) => values.TryGetValue(key, out var value) && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed) ? parsed : fallback;
    static bool Bool(Dictionary<string, object> values, string key, bool fallback) => values.TryGetValue(key, out var value) && bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed) ? parsed : fallback;
    static string[] Array(Dictionary<string, object> values, string key) => values.TryGetValue(key, out var value) && value is string[] array ? array : [];
    static bool IsNumeric(string value) => value.Length > 0 && value.All(char.IsDigit);
    static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>Small strict TOML reader supporting the scalar and string-array subset used by this project.</summary>
internal static class SimpleToml
{
    public static Dictionary<string, object> Read(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Configuration file not found: {path}", path);
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        var lineNumber = 0;
        foreach (var sourceLine in File.ReadLines(path, Encoding.UTF8))
        {
            lineNumber++;
            var line = StripComment(sourceLine).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (section.Length == 0) Fail(path, lineNumber, "empty section");
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals <= 0) Fail(path, lineNumber, "expected key = value");
            var key = line[..equals].Trim();
            var fullKey = string.IsNullOrEmpty(section) ? key : $"{section}.{key}";
            result[fullKey] = ParseValue(path, lineNumber, line[(equals + 1)..].Trim());
        }
        return result;
    }

    public static void Overlay(Dictionary<string, object> target, Dictionary<string, object> overlay)
    {
        foreach (var pair in overlay) target[pair.Key] = pair.Value;
    }

    static object ParseValue(string path, int line, string value)
    {
        if (value.StartsWith('"') && value.EndsWith('"')) return Unquote(value);
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            var inner = value[1..^1].Trim();
            if (inner.Length == 0) return Array.Empty<string>();
            return inner.Split(',').Select(item =>
            {
                var trimmed = item.Trim();
                if (!trimmed.StartsWith('"') || !trimmed.EndsWith('"')) Fail(path, line, "arrays must contain quoted strings");
                return Unquote(trimmed);
            }).ToArray();
        }
        if (bool.TryParse(value, out var boolean)) return boolean;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
        Fail(path, line, $"unsupported value '{value}'");
        return "";
    }

    static string Unquote(string value) => value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
    static string StripComment(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) quoted = !quoted;
            if (line[i] == '#' && !quoted) return line[..i];
        }
        return line;
    }
    static void Fail(string path, int line, string message) => throw new InvalidDataException($"{path}:{line}: {message}");
}
