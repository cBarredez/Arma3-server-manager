using System.Text.RegularExpressions;
using Arma3Manager.Api.Configuration;
using Arma3Manager.Api.Contracts;
using Arma3Manager.Api.Domain;

namespace Arma3Manager.Api.Application;

/// <summary>Builds shell-free argument lists for the Arma 3 server process.</summary>
public static class CommandBuilder
{
    public static string Build(ServerPaths paths, StartupSettings settings, IEnumerable<string>? mods = null) => "./" + settings.ServerBinary + " " + string.Join(' ', Args(paths, settings, mods ?? []).Select(Quote));
    public static IEnumerable<string> Args(ServerPaths paths, StartupSettings settings, IEnumerable<string> mods)
    {
        var ip = (settings.Ip ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(ip) && ip != "0.0.0.0") yield return $"-ip={ip}";
        yield return $"-port={settings.Port}";
        yield return $"-config={settings.ServerCfg}";
        yield return $"-cfg={settings.BasicCfg}";
        yield return $"-profiles={settings.ProfilesDir}";
        yield return "-noSplash";
        yield return "-noPause";
        yield return "-world=empty";
        var modList = mods.Select(mod => Path.GetRelativePath(paths.Arma3Dir, mod)).ToArray();
        if (modList.Length > 0) yield return $"-mod={string.Join(';', modList)}";
        if (!string.IsNullOrWhiteSpace(settings.ServerMods)) yield return $"-serverMod={settings.ServerMods}";
        if (settings.DisableBattleEye) yield return "-noBattlEye";
        foreach (var argument in SplitArgs(settings.ExtraParams)) yield return argument;
    }
    public static IEnumerable<string> SplitArgs(string value) => Regex.Matches(value ?? "", @"[^\s""]+|""([^""]*)""").Select(match => match.Value.Trim('"'));
    static string Quote(string argument) => argument.Any(char.IsWhiteSpace) ? $"\"{argument.Replace("\"", "\\\"")}\"" : argument;
}

/// <summary>Restricts requested paths to a configured filesystem root.</summary>
public static class PathGuard
{
    public static string Resolve(string root, string? requestedPath)
    {
        var fullRoot = Path.GetFullPath(root);
        var raw = (requestedPath ?? "").Trim();
        var resolved = Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(fullRoot, raw));
        var boundary = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (resolved != fullRoot && !resolved.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Access denied");
        return resolved;
    }
    public static string Relative(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(fullPath)).Replace('\\', '/');
        return relative == "." ? "" : relative;
    }
}

public static class PresetFiles
{
    public static string Root(AppConfig config) => Path.Combine(config.Arma3Dir, "presets", "modlists");
    public static async Task<string> SaveAsync(AppConfig config, IFormFile file)
    {
        Directory.CreateDirectory(Root(config));
        var name = SafeName(string.IsNullOrWhiteSpace(file.FileName) ? "preset.html" : file.FileName);
        if (!name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)) name += ".html";
        var path = Path.Combine(Root(config), $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{name}");
        await using var output = File.Create(path);
        await file.CopyToAsync(output);
        return path;
    }
    public static SavedPresetFile[] List(AppConfig config)
    {
        Directory.CreateDirectory(Root(config));
        return Directory.EnumerateFiles(Root(config), "*.htm*").Select(path =>
        {
            var info = new FileInfo(path);
            return new SavedPresetFile(info.Name, PathGuard.Relative(config.Arma3Dir, path), info.Length, info.LastWriteTimeUtc);
        }).OrderByDescending(file => file.Modified).ToArray();
    }
    public static string Resolve(AppConfig config, string path)
    {
        var file = PathGuard.Resolve(config.Arma3Dir, path);
        var root = Path.GetFullPath(Root(config));
        var full = Path.GetFullPath(file);
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Access denied");
        if (!File.Exists(full)) throw new FileNotFoundException("Preset file not found");
        return full;
    }
    static string SafeName(string name)
    {
        var clean = Path.GetFileName(name);
        foreach (var character in Path.GetInvalidFileNameChars()) clean = clean.Replace(character, '-');
        return Regex.Replace(clean, @"\s+", "-");
    }
}

public static class ProtectedFiles
{
    static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase) { "manager.sqlite3", "manager.sqlite3-shm", "manager.sqlite3-wal" };
    public static bool IsProtected(string relativePath) => Names.Contains(relativePath.Replace('\\', '/').Trim('/'));
}

public static class PresetParser
{
    public static List<PresetMod> Parse(string html) => Regex.Matches(html, @"[?&]id=(\d{6,12})").Select(match => match.Groups[1].Value).Distinct().Select(id => new PresetMod($"@{id}", id)).ToList();
}

public static class ServerCfgWriter
{
    public static async Task ApplyAsync(StartupSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ServerCfg) || !File.Exists(settings.ServerCfg)) return;
        var text = await File.ReadAllTextAsync(settings.ServerCfg);
        if (settings.MaxPlayers is not null) text = Regex.Replace(text, @"maxPlayers\s*=\s*\d+\s*;", $"maxPlayers = {settings.MaxPlayers};");
        if (settings.ServerPassword is not null) text = Regex.Replace(text, @"password\s*=\s*""[^""]*""\s*;", $"password = \"{settings.ServerPassword}\";");
        await File.WriteAllTextAsync(settings.ServerCfg, text);
    }
}
