using System.Diagnostics;
using Arma3Manager.Api.Configuration;
using Arma3Manager.Api.Contracts;

namespace Arma3Manager.Api.Infrastructure;

/// <summary>Reads resource metrics from the active cgroup and filesystem.</summary>
public static class MetricsReader
{
    public static object ReadMemory()
    {
        var used = ReadLong("/sys/fs/cgroup/memory.current") ?? Process.GetCurrentProcess().WorkingSet64;
        var total = ReadLong("/sys/fs/cgroup/memory.max") ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (total <= 0) total = used;
        var free = Math.Max(total - used, 0);
        var percent = total > 0 ? Math.Clamp(Math.Round(used * 100d / total, 1), 0, 100) : 0;
        return new { total, used, free, percent };
    }

    public static object ReadDisk(string path, string mountLabel)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(fullPath);
        var drive = new DriveInfo(Path.GetPathRoot(fullPath) ?? "/");
        var size = drive.TotalSize;
        var available = drive.AvailableFreeSpace;
        var used = size - available;
        var percent = size > 0 ? Math.Clamp(Math.Round(used * 100d / size, 1), 0, 100) : 0;
        return new { fs = drive.Name, mount = mountLabel, size, used, available, percent };
    }

    static long? ReadLong(string path)
    {
        if (!File.Exists(path)) return null;
        var value = File.ReadAllText(path).Trim();
        if (value.Equals("max", StringComparison.OrdinalIgnoreCase)) return null;
        return long.TryParse(value, out var parsed) ? parsed : null;
    }
}

/// <summary>Known Creator DLC folders supported by the manager.</summary>
public static class CreatorDlcCatalog
{
    static readonly (string Id, string Name, string Folder)[] Known =
    [
        ("gm", "Global Mobilization", "gm"), ("vn", "S.O.G. Prairie Fire", "vn"),
        ("csla", "CSLA Iron Curtain", "csla"), ("ws", "Western Sahara", "ws"),
        ("spe", "Spearhead 1944", "spe"), ("rf", "Reaction Forces", "rf"),
        ("ef", "Expeditionary Forces", "ef")
    ];

    public static List<CreatorDlc> List(AppConfig cfg) => Known.Select(item =>
    {
        var path = Path.Combine(cfg.Arma3Dir, item.Folder);
        return new CreatorDlc(item.Id, item.Name, item.Folder, path, Directory.Exists(path), false);
    }).ToList();
}

/// <summary>Formats commands for audit logs without invoking a shell.</summary>
public static class CommandLog
{
    public static string Format(string file, IEnumerable<string> args) => $"{file} {string.Join(' ', args.Select(Quote))}";
    static string Quote(string arg) => arg.Any(char.IsWhiteSpace) ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;
}

/// <summary>Repairs case-sensitive mod paths required by the Linux Arma runtime.</summary>
public static class ModFileRepair
{
    public static int MakeLowercase(IEnumerable<string> modPaths) => modPaths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Sum(LowercaseTree);

    static int LowercaseTree(string root)
    {
        var changed = Directory.Exists(root)
            ? Directory.EnumerateFileSystemEntries(root).OrderByDescending(path => path.Count(character => character == Path.DirectorySeparatorChar)).Sum(LowercaseTree)
            : 0;
        return changed + LowercaseEntry(root);
    }

    static int LowercaseEntry(string path)
    {
        var parent = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name)) return 0;
        var lower = name.ToLowerInvariant();
        if (name == lower) return 0;
        var target = Path.Combine(parent, lower);
        if (Path.Exists(target) && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.Ordinal)) return 0;
        var temporary = Path.Combine(parent, $".a3mgr-lower-{Guid.NewGuid():N}");
        if (Directory.Exists(path)) { Directory.Move(path, temporary); Directory.Move(temporary, target); }
        else if (File.Exists(path)) { File.Move(path, temporary); File.Move(temporary, target); }
        else return 0;
        return 1;
    }
}
