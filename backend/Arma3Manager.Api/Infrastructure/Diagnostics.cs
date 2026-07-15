using System.Diagnostics;
using System.Globalization;
using Arma3Manager.Api.Application;
using Arma3Manager.Api.Configuration;
using Arma3Manager.Api.Contracts;
using Arma3Manager.Api.Infrastructure.Persistence;

namespace Arma3Manager.Api.Infrastructure;

/// <summary>Cached CPU and temperature values exposed by the metrics endpoint.</summary>
public sealed record CpuMetrics(double? Load, double[] Cores);
public sealed record HostMetricsSample(CpuMetrics Cpu, double? Temperature);
public sealed record MemoryMetrics(long Total, long Used, long Free, long Cache, long Current, double Percent);

/// <summary>Samples cumulative cgroup counters independently from HTTP polling, and — while a game session is
/// running — records a lower-frequency history of those samples per RunId so CPU/RAM usage for a match can be
/// reviewed or exported after the fact (issue: "Añadir métricas solicitadas").</summary>
public sealed class MetricsSampler(ILogger<MetricsSampler> logger, RuntimeState runtime, SqliteStore store, AppConfig config) : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(5);
    HostMetricsSample current = new(new CpuMetrics(null, []), null);
    DateTime lastPersistedUtc = DateTime.MinValue;

    public HostMetricsSample Current => Volatile.Read(ref current);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var previousUsage = MetricsReader.ReadCpuUsageMicroseconds();
        var previousTimestamp = Stopwatch.GetTimestamp();
        Volatile.Write(ref current, new HostMetricsSample(new CpuMetrics(null, []), MetricsReader.ReadCpuTemperature()));
        await PruneOldSessionsAsync();

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var usage = MetricsReader.ReadCpuUsageMicroseconds();
                var timestamp = Stopwatch.GetTimestamp();
                var capacity = MetricsReader.ReadCpuCapacity();
                var load = previousUsage.HasValue && usage.HasValue
                    ? MetricsReader.CalculateCpuLoad(previousUsage.Value, usage.Value, Stopwatch.GetElapsedTime(previousTimestamp, timestamp), capacity)
                    : null;

                Volatile.Write(ref current, new HostMetricsSample(new CpuMetrics(load, []), MetricsReader.ReadCpuTemperature()));
                previousUsage = usage;
                previousTimestamp = timestamp;

                await PersistIfDueAsync(load, capacity);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Unable to sample system metrics");
                Volatile.Write(ref current, new HostMetricsSample(new CpuMetrics(null, []), null));
                previousUsage = MetricsReader.ReadCpuUsageMicroseconds();
                previousTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    async Task PersistIfDueAsync(double? load, double capacity)
    {
        if (!runtime.IsRunning || runtime.RunId is null) return;
        var now = DateTime.UtcNow;
        if (now - lastPersistedUtc < PersistInterval) return;
        lastPersistedUtc = now;

        try
        {
            var memory = MetricsReader.ReadMemory();
            await store.InsertMetricsSampleAsync(new MetricsSample(runtime.RunId, DateTimeOffset.UtcNow, load, capacity, memory.Used, memory.Percent));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to persist session metrics sample");
        }
    }

    async Task PruneOldSessionsAsync()
    {
        try { await store.PruneHistoryAsync(config.HistoryRetentionDays); }
        catch (Exception exception) { logger.LogWarning(exception, "Unable to prune expired session history"); }
    }
}

/// <summary>Reads resource metrics from the active cgroup and filesystem.</summary>
public static class MetricsReader
{
    const string DefaultCgroupRoot = "/sys/fs/cgroup";
    static readonly string[] DefaultSysRoots = ["/host-sys", "/sys"];

    public static MemoryMetrics ReadMemory(string cgroupRoot = DefaultCgroupRoot)
    {
        var current = ReadLong(Path.Combine(cgroupRoot, "memory.current")) ?? Process.GetCurrentProcess().WorkingSet64;
        var total = ReadLong(Path.Combine(cgroupRoot, "memory.max")) ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var cache = ReadMemoryStat(cgroupRoot, "inactive_file") ?? 0;
        cache = Math.Clamp(cache, 0, current);
        var used = Math.Max(current - cache, 0);
        if (total <= 0) total = current;
        var free = Math.Max(total - used, 0);
        var percent = total > 0 ? Math.Clamp(Math.Round(used * 100d / total, 1), 0, 100) : 0;
        return new(total, used, free, cache, current, percent);
    }

    static long? ReadMemoryStat(string cgroupRoot, string key)
    {
        var text = ReadText(Path.Combine(cgroupRoot, "memory.stat"));
        if (text is null) return null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2 && fields[0] == key && long.TryParse(fields[1], CultureInfo.InvariantCulture, out var value) && value >= 0)
                return value;
        }
        return null;
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

    public static long? ReadCpuUsageMicroseconds(string cgroupRoot = DefaultCgroupRoot)
    {
        var text = ReadText(Path.Combine(cgroupRoot, "cpu.stat"));
        if (text is null) return null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2 && fields[0] == "usage_usec" && long.TryParse(fields[1], CultureInfo.InvariantCulture, out var usage) && usage >= 0)
                return usage;
        }
        return null;
    }

    public static double ReadCpuCapacity(string cgroupRoot = DefaultCgroupRoot, int fallbackProcessorCount = 0)
    {
        var cpuMax = ReadText(Path.Combine(cgroupRoot, "cpu.max"));
        if (cpuMax is not null)
        {
            var fields = cpuMax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && fields[0] != "max"
                && double.TryParse(fields[0], CultureInfo.InvariantCulture, out var quota)
                && double.TryParse(fields[1], CultureInfo.InvariantCulture, out var period)
                && quota > 0 && period > 0 && double.IsFinite(quota) && double.IsFinite(period))
                return quota / period;
        }

        var cpuSet = ReadText(Path.Combine(cgroupRoot, "cpuset.cpus.effective"));
        var assigned = cpuSet is null ? 0 : CountCpuSet(cpuSet);
        if (assigned > 0) return assigned;
        return Math.Max(fallbackProcessorCount > 0 ? fallbackProcessorCount : Environment.ProcessorCount, 1);
    }

    public static double? CalculateCpuLoad(long previousUsageMicroseconds, long currentUsageMicroseconds, TimeSpan elapsed, double capacity)
    {
        var usageDelta = currentUsageMicroseconds - previousUsageMicroseconds;
        if (usageDelta < 0 || elapsed <= TimeSpan.Zero || capacity <= 0 || !double.IsFinite(capacity)) return null;
        var availableMicroseconds = elapsed.TotalMilliseconds * 1000d * capacity;
        if (availableMicroseconds <= 0 || !double.IsFinite(availableMicroseconds)) return null;
        return Math.Clamp(Math.Round(usageDelta * 100d / availableMicroseconds, 1), 0, 100);
    }

    public static double? ReadCpuTemperature(params string[] sysRoots)
    {
        foreach (var root in sysRoots.Length > 0 ? sysRoots : DefaultSysRoots)
        {
            var hwmon = ReadHwmonTemperature(root);
            if (hwmon.HasValue) return hwmon;
            var thermal = ReadThermalZoneTemperature(root);
            if (thermal.HasValue) return thermal;
        }
        return null;
    }

    static double? ReadHwmonTemperature(string sysRoot)
    {
        var root = Path.Combine(sysRoot, "class", "hwmon");
        if (!Directory.Exists(root)) return null;
        var candidates = new List<(int Score, double Temperature)>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "hwmon*"))
            {
                var chip = (ReadText(Path.Combine(directory, "name")) ?? "").Trim().ToLowerInvariant();
                var chipScore = chip switch
                {
                    "coretemp" or "k10temp" or "zenpower" => 100,
                    "cpu_thermal" or "cpu-thermal" => 90,
                    "soc_thermal" or "soc-thermal" => 80,
                    _ => 0
                };
                if (chipScore == 0) continue;

                foreach (var input in Directory.EnumerateFiles(directory, "temp*_input"))
                {
                    var label = (ReadText(input[..^"_input".Length] + "_label") ?? "").Trim().ToLowerInvariant();
                    var labelScore = label.Contains("package", StringComparison.Ordinal) || label is "tctl" or "tdie" ? 30
                        : label.Contains("cpu", StringComparison.Ordinal) ? 20
                        : label.Contains("core", StringComparison.Ordinal) ? 10
                        : 0;
                    var temperature = ReadTemperatureValue(input);
                    if (temperature.HasValue) candidates.Add((chipScore + labelScore, temperature.Value));
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return candidates.Count == 0 ? null : candidates.OrderByDescending(candidate => candidate.Score).ThenByDescending(candidate => candidate.Temperature).First().Temperature;
    }

    static double? ReadThermalZoneTemperature(string sysRoot)
    {
        var root = Path.Combine(sysRoot, "class", "thermal");
        if (!Directory.Exists(root)) return null;
        var candidates = new List<(int Score, double Temperature)>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "thermal_zone*"))
            {
                var type = (ReadText(Path.Combine(directory, "type")) ?? "").Trim().ToLowerInvariant();
                var score = type.Contains("x86_pkg_temp", StringComparison.Ordinal) ? 100
                    : type.Contains("cpu", StringComparison.Ordinal) ? 90
                    : type.Contains("soc_thermal", StringComparison.Ordinal) || type.Contains("soc-thermal", StringComparison.Ordinal) ? 80
                    : 0;
                if (score == 0) continue;
                var temperature = ReadTemperatureValue(Path.Combine(directory, "temp"));
                if (temperature.HasValue) candidates.Add((score, temperature.Value));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return candidates.Count == 0 ? null : candidates.OrderByDescending(candidate => candidate.Score).ThenByDescending(candidate => candidate.Temperature).First().Temperature;
    }

    static double? ReadTemperatureValue(string path)
    {
        var text = ReadText(path);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw)) return null;
        var temperature = Math.Abs(raw) > 1000 ? raw / 1000d : raw;
        return double.IsFinite(temperature) && temperature is >= -50 and <= 150 ? Math.Round(temperature, 1) : null;
    }

    static int CountCpuSet(string value)
    {
        var cpus = new HashSet<int>();
        foreach (var part in value.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (!int.TryParse(bounds[0], out var first) || first < 0) continue;
            int last;
            if (bounds.Length == 1) last = first;
            else if (bounds.Length == 2 && int.TryParse(bounds[1], out var parsed)) last = parsed;
            else continue;
            if (last < first || (long)last - first > 65535) continue;
            for (var cpu = first; ; cpu++)
            {
                cpus.Add(cpu);
                if (cpu == last) break;
            }
        }
        return cpus.Count;
    }

    static long? ReadLong(string path)
    {
        if (!File.Exists(path)) return null;
        var value = File.ReadAllText(path).Trim();
        if (value.Equals("max", StringComparison.OrdinalIgnoreCase)) return null;
        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
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
