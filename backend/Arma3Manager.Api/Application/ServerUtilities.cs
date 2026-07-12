using System.Text.RegularExpressions;
using Arma3Manager.Api.Configuration;
using Arma3Manager.Api.Contracts;
using Arma3Manager.Api.Domain;
using Arma3Manager.Api.Infrastructure;

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
    static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase) { "manager.sqlite3", "manager.sqlite3-shm", "manager.sqlite3-wal", FactoryResetExecutor.MarkerName };
    public static bool IsProtected(string relativePath) => Names.Contains(relativePath.Replace('\\', '/').Trim('/'));
}

public static class PresetParser
{
    public static List<PresetMod> Parse(string html) => Regex.Matches(html, @"[?&]id=(\d{6,12})").Select(match => match.Groups[1].Value).Distinct().Select(id => new PresetMod($"@{id}", id)).ToList();
}

public sealed record WorkshopStorageStatus(int DuplicateCopies);
public sealed record WorkshopStorageRepairResult(int Converted, long ReclaimedBytes);

public static class WorkshopStorage
{
    public static string Source(AppConfig config, string workshopId) =>
        Path.Combine(config.Arma3Dir, "steamapps", "workshop", "content", "107410", workshopId);

    public static string Reference(AppConfig config, string workshopId) =>
        Path.Combine(config.Arma3Dir, $"@{workshopId}");

    public static bool IsInstalled(AppConfig config, string workshopId) =>
        Directory.Exists(Source(config, workshopId));

    public static string EnsureReference(AppConfig config, string workshopId)
    {
        var source = Source(config, workshopId);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Workshop mod {workshopId} is not installed");
        var target = Reference(config, workshopId);
        if (Directory.Exists(target)) return IsSymbolicLink(target) ? target : source;

        try
        {
            Directory.CreateSymbolicLink(target, source);
            return target;
        }
        catch
        {
            // Arma accepts a relative Workshop path. Never duplicate a full mod just because links are unavailable.
            return source;
        }
    }

    public static WorkshopStorageStatus Status(AppConfig config)
    {
        var root = Path.Combine(config.Arma3Dir, "steamapps", "workshop", "content", "107410");
        if (!Directory.Exists(root)) return new(0);
        var duplicates = Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(id => Regex.IsMatch(id ?? "", @"^\d+$"))
            .Count(id => Directory.Exists(Reference(config, id!)) && !IsSymbolicLink(Reference(config, id!)));
        return new(duplicates);
    }

    public static WorkshopStorageRepairResult RepairDuplicates(AppConfig config)
    {
        var root = Path.Combine(config.Arma3Dir, "steamapps", "workshop", "content", "107410");
        if (!Directory.Exists(root)) return new(0, 0);
        var converted = 0;
        long reclaimed = 0;

        foreach (var source in Directory.EnumerateDirectories(root))
        {
            var id = Path.GetFileName(source);
            if (!Regex.IsMatch(id, @"^\d+$")) continue;
            var target = Reference(config, id);
            if (!Directory.Exists(target) || IsSymbolicLink(target)) continue;
            var temporary = target + $".a3mgr-link-{Guid.NewGuid():N}";

            try
            {
                Directory.CreateSymbolicLink(temporary, source);
                var bytes = DirectorySize(target);
                Directory.Delete(target, true);
                Directory.Move(temporary, target);
                reclaimed += bytes;
                converted++;
            }
            catch
            {
                if (Directory.Exists(temporary)) Directory.Delete(temporary);
            }
        }

        return new(converted, reclaimed);
    }

    public static void Delete(AppConfig config, string workshopId)
    {
        var reference = Reference(config, workshopId);
        if (Directory.Exists(reference))
        {
            if (IsSymbolicLink(reference)) Directory.Delete(reference);
            else Directory.Delete(reference, true);
        }
        var source = Source(config, workshopId);
        if (Directory.Exists(source)) Directory.Delete(source, true);
    }

    public static bool IsSymbolicLink(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget is not null; }
        catch { return false; }
    }

    static long DirectorySize(string path)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path)) total += new FileInfo(file).Length;
        foreach (var directory in Directory.EnumerateDirectories(path))
            if (!IsSymbolicLink(directory)) total += DirectorySize(directory);
        return total;
    }
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

public static class MissionConfig
{
    public static MissionEntry[] List(ServerPaths paths)
    {
        if (!Directory.Exists(paths.MissionsDir)) return [];
        var entries = new List<MissionEntry>();
        foreach (var file in Directory.EnumerateFiles(paths.MissionsDir, "*", SearchOption.TopDirectoryOnly))
        {
            if (!file.EndsWith(".pbo", StringComparison.OrdinalIgnoreCase)) continue;
            var info = new FileInfo(file);
            var name = info.Name[..^4];
            entries.Add(new(name, info.Name, true, info.Length, info.LastWriteTimeUtc));
        }
        foreach (var directory in Directory.EnumerateDirectories(paths.MissionsDir, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new DirectoryInfo(directory);
            entries.Add(new(info.Name, info.Name, false, 0, info.LastWriteTimeUtc));
        }
        return entries
            .GroupBy(entry => entry.Template, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(entry => entry.Packed).First())
            .OrderBy(entry => entry.Template, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string? ReadSelected(string serverCfg)
    {
        if (!File.Exists(serverCfg)) return null;
        var text = File.ReadAllText(serverCfg);
        if (!TryFindClassBody(text, "Missions", 0, text.Length, out var missionsStart, out var missionsEnd) ||
            !TryFindClassBody(text, "Mission1", missionsStart, missionsEnd, out var missionStart, out var missionEnd)) return null;
        var match = Regex.Match(text[missionStart..missionEnd], @"\btemplate\s*=\s*\""([^\""\r\n]+)\""\s*;", RegexOptions.IgnoreCase);
        return match.Success ? StripPbo(match.Groups[1].Value) : null;
    }

    public static async Task ApplyAsync(string serverCfg, string template)
    {
        template = StripPbo(Path.GetFileName(template.Trim()));
        if (string.IsNullOrWhiteSpace(template) || template.IndexOfAny(['\"', '\r', '\n']) >= 0)
            throw new InvalidDataException("Invalid mission template");

        var text = await File.ReadAllTextAsync(serverCfg);
        if (TryFindClassBody(text, "Missions", 0, text.Length, out var missionsStart, out var missionsEnd) &&
            TryFindClassBody(text, "Mission1", missionsStart, missionsEnd, out var missionStart, out var missionEnd))
        {
            var body = text[missionStart..missionEnd];
            var pattern = @"\btemplate\s*=\s*\""[^\""\r\n]*\""\s*;";
            var replacement = $"template = \"{template}\";";
            body = Regex.IsMatch(body, pattern, RegexOptions.IgnoreCase)
                ? Regex.Replace(body, pattern, replacement, RegexOptions.IgnoreCase)
                : $"\n        {replacement}{body}";
            text = text[..missionStart] + body + text[missionEnd..];
        }
        else
        {
            text = text.TrimEnd() + $"\n\nclass Missions\n{{\n    class Mission1\n    {{\n        template = \"{template}\";\n        difficulty = \"Custom\";\n    }};\n}};\n";
        }
        await File.WriteAllTextAsync(serverCfg, text);
    }

    static string StripPbo(string value) => value.EndsWith(".pbo", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;

    static bool TryFindClassBody(string text, string className, int start, int end, out int bodyStart, out int bodyEnd)
    {
        var match = Regex.Match(text[start..end], $@"\bclass\s+{Regex.Escape(className)}\b[^{{;]*{{", RegexOptions.IgnoreCase);
        if (!match.Success) { bodyStart = bodyEnd = -1; return false; }
        var opening = start + match.Index + match.Length - 1;
        var depth = 1;
        for (var i = opening + 1; i < end; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) { bodyStart = opening + 1; bodyEnd = i; return true; }
        }
        bodyStart = bodyEnd = -1;
        return false;
    }
}

/// <summary>Writes or removes the BattlEye RCon config BE discovers under &lt;profiles&gt;/battleye/BEServer.cfg.</summary>
public static class BattlEyeConfigWriter
{
    public static async Task ApplyAsync(ServerPaths paths, AppConfig cfg)
    {
        var directory = Path.Combine(paths.ProfilesDir, "battleye");
        var file = Path.Combine(directory, "BEServer.cfg");
        if (string.IsNullOrWhiteSpace(cfg.RconPassword))
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(file, $"RConPassword {cfg.RconPassword}\nRConPort {cfg.RconPort}\n");
    }
}
