using Arma3Manager.Api.Application;
using Arma3Manager.Api.Contracts;
using Arma3Manager.Api.Infrastructure;
using Arma3Manager.Api.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arma3Manager.Api.Tests;

public sealed class MetricsTests
{
    [Fact]
    public void ReadsUsageFromCgroupV2CpuStat()
    {
        using var fixture = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(fixture.Path, "cpu.stat"), "user_usec 100\nusage_usec 123456\nsystem_usec 200\n");

        Assert.Equal(123456, MetricsReader.ReadCpuUsageMicroseconds(fixture.Path));
    }

    [Fact]
    public void InvalidOrMissingCpuStatReturnsNull()
    {
        using var fixture = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(fixture.Path, "cpu.stat"), "usage_usec invalid\n");

        Assert.Null(MetricsReader.ReadCpuUsageMicroseconds(fixture.Path));
        Assert.Null(MetricsReader.ReadCpuUsageMicroseconds(Path.Combine(fixture.Path, "missing")));
    }

    [Fact]
    public void MemoryUsageExcludesReclaimableInactiveFileCache()
    {
        using var fixture = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(fixture.Path, "memory.current"), "12000000000\n");
        File.WriteAllText(Path.Combine(fixture.Path, "memory.max"), "14000000000\n");
        File.WriteAllText(Path.Combine(fixture.Path, "memory.stat"), "anon 2000000000\ninactive_file 9000000000\nactive_file 500000000\n");

        var memory = MetricsReader.ReadMemory(fixture.Path);

        Assert.Equal(3_000_000_000, memory.Used);
        Assert.Equal(9_000_000_000, memory.Cache);
        Assert.Equal(12_000_000_000, memory.Current);
        Assert.Equal(21.4, memory.Percent);
    }

    [Fact]
    public void MemoryUsageNeverBecomesNegativeWhenStatsAreInconsistent()
    {
        using var fixture = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(fixture.Path, "memory.current"), "1000\n");
        File.WriteAllText(Path.Combine(fixture.Path, "memory.max"), "2000\n");
        File.WriteAllText(Path.Combine(fixture.Path, "memory.stat"), "inactive_file 5000\n");

        var memory = MetricsReader.ReadMemory(fixture.Path);

        Assert.Equal(0, memory.Used);
        Assert.Equal(1000, memory.Cache);
        Assert.Equal(0, memory.Percent);
    }

    [Fact]
    public void CpuQuotaTakesPrecedenceOverCpuSet()
    {
        using var fixture = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(fixture.Path, "cpu.max"), "200000 100000\n");
        File.WriteAllText(Path.Combine(fixture.Path, "cpuset.cpus.effective"), "0-7\n");

        Assert.Equal(2, MetricsReader.ReadCpuCapacity(fixture.Path, 12));
    }

    [Fact]
    public void CpuSetSupportsRangesAndIndividualCpus()
    {
        using var fixture = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(fixture.Path, "cpu.max"), "max 100000\n");
        File.WriteAllText(Path.Combine(fixture.Path, "cpuset.cpus.effective"), "0-3,6,8-9\n");

        Assert.Equal(7, MetricsReader.ReadCpuCapacity(fixture.Path, 12));
    }

    [Fact]
    public void CpuCapacityUsesFallbackWhenCgroupFilesAreInvalid()
    {
        using var fixture = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(fixture.Path, "cpu.max"), "invalid\n");
        File.WriteAllText(Path.Combine(fixture.Path, "cpuset.cpus.effective"), "invalid,1-x\n");

        Assert.Equal(6, MetricsReader.ReadCpuCapacity(fixture.Path, 6));
    }

    [Theory]
    [InlineData(1_000_000, 1_500_000, 1, 1, 50d)]
    [InlineData(1_000_000, 3_500_000, 1, 2, 100d)]
    [InlineData(3_000_000, 2_000_000, 1, 1, null)]
    [InlineData(1_000_000, 1_500_000, 0, 1, null)]
    public void CalculatesNormalizedCpuLoad(long previous, long current, double seconds, double capacity, double? expected)
    {
        Assert.Equal(expected, MetricsReader.CalculateCpuLoad(previous, current, TimeSpan.FromSeconds(seconds), capacity));
    }

    [Fact]
    public void CalculatesPodmanCpuUsageAndEquivalentCoresWithoutNormalizingToCapacity()
    {
        var usage = MetricsReader.CalculateCpuUsagePercent(1_000_000, 3_500_000, TimeSpan.FromSeconds(1));
        var sample = new MetricsSample("run", DateTimeOffset.UtcNow, 15.6, 16, 1000, 10, usage);

        Assert.Equal(250, usage);
        Assert.Equal(2.5, sample.CpuCoresUsed);
        Assert.Equal(15.6, MetricsReader.NormalizeCpuLoad(usage!.Value, 16));
    }

    [Theory]
    [InlineData(3_000_000, 2_000_000, 1)]
    [InlineData(1_000_000, 1_500_000, 0)]
    public void InvalidPodmanCpuDeltasReturnNull(long previous, long current, double seconds)
    {
        Assert.Null(MetricsReader.CalculateCpuUsagePercent(previous, current, TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task MetricsStoreAggregatesCpuPopulationAndFiltersByRunPrefix()
    {
        using var fixture = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(fixture.Path, "manager.sqlite3"));
        await store.InitAsync();
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        await store.StartServerSessionAsync(new ServerRunStarted("abc123-run", started, 42));
        await store.InsertMetricsSampleAsync(new MetricsSample("abc123-run", started.AddSeconds(5), 14.7, 16, 1_000_000, 40, 235, 18, 2));
        await store.InsertMetricsSampleAsync(new MetricsSample("abc123-run", started.AddSeconds(10), 9.4, 16, 1_200_000, 55, 150, 24, 3));
        await store.EndServerSessionAsync(new ServerRunEnded("abc123-run", started.AddMinutes(5), 0, "stopped"));

        var page = await store.GetServerSessionsAsync(null, null, "ended", null, 10, "abc123", "newest");
        var session = Assert.Single(page.Items);
        var detail = await store.GetMetricsSessionDetailAsync(session.RunId);

        Assert.Equal(2, session.SampleCount);
        Assert.Equal(192.5, session.AvgCpuUsagePercent);
        Assert.Equal(235, session.PeakCpuUsagePercent);
        Assert.Equal(2.35, session.PeakCpuCoresUsed);
        Assert.Equal(24, session.PeakActivePlayers);
        Assert.Equal(3, session.PeakActiveHeadlessClients);
        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Samples.Count);
        Assert.Equal(18, detail.Samples[0].ActivePlayers);
    }

    [Fact]
    public async Task MetricsMigrationBackfillsPodmanCpuButLeavesPopulationUnknown()
    {
        using var fixture = new TemporaryDirectory();
        var dbPath = Path.Combine(fixture.Path, "manager.sqlite3");
        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
            create table schema_migrations(version integer primary key,applied_at text not null);
            insert into schema_migrations values(1,'2026-01-01'),(2,'2026-01-01'),(3,'2026-01-01'),(4,'2026-01-01');
            create table metrics_samples(id integer primary key autoincrement,run_id text not null,sampled_at text not null,cpu_percent real null,cores_capacity real not null,memory_used_bytes integer not null,memory_percent real not null);
            insert into metrics_samples(run_id,sampled_at,cpu_percent,cores_capacity,memory_used_bytes,memory_percent)
            values('legacy-run','2026-01-01T00:00:00.0000000+00:00',12.5,16,1024,50);
            """;
            await command.ExecuteNonQueryAsync();
        }

        var store = new SqliteStore(dbPath);
        await store.InitAsync();
        var sample = Assert.Single(await store.GetMetricsSamplesAsync("legacy-run"));

        Assert.Equal(200, sample.CpuUsagePercent);
        Assert.Equal(2, sample.CpuCoresUsed);
        Assert.Null(sample.ActivePlayers);
        Assert.Null(sample.ActiveHeadlessClients);
    }

    [Fact]
    public void CsvExportsCorrelatedMetricsAndPreservesUnavailableCounts()
    {
        var at = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var csv = MetricsCsv.Create([
            new MetricsSample("run", at, 14.7, 16, 157_286_400, 62.5, 235, 18, 2),
            new MetricsSample("legacy", at.AddSeconds(5), 12.5, 16, 104_857_600, 50, 200)
        ]);

        Assert.StartsWith("timestamp_utc,cpu_usage_percent,cpu_cores_used,cpu_load_percent,cores_capacity,memory_used_mb,memory_percent,active_players,active_headless_clients", csv);
        Assert.Contains(",235,2.350,14.7,16,150.0,62.5,18,2", csv);
        Assert.Contains(",200,2.000,12.5,16,100.0,50,,", csv);
    }

    [Fact]
    public void HwmonPrefersPackageTemperatureOverCoreTemperature()
    {
        using var fixture = new TemporaryDirectory();
        var hwmon = Directory.CreateDirectory(Path.Combine(fixture.Path, "class", "hwmon", "hwmon0")).FullName;
        File.WriteAllText(Path.Combine(hwmon, "name"), "coretemp\n");
        WriteSensor(hwmon, 1, "Package id 0", "62500");
        WriteSensor(hwmon, 2, "Core 0", "71000");

        Assert.Equal(62.5, MetricsReader.ReadCpuTemperature(fixture.Path));
    }

    [Fact]
    public void HwmonSupportsAmdLabelsAndRejectsImpossibleValues()
    {
        using var fixture = new TemporaryDirectory();
        var hwmon = Directory.CreateDirectory(Path.Combine(fixture.Path, "class", "hwmon", "hwmon0")).FullName;
        File.WriteAllText(Path.Combine(hwmon, "name"), "k10temp\n");
        WriteSensor(hwmon, 1, "Tctl", "175000");
        WriteSensor(hwmon, 2, "Tdie", "48750");

        Assert.Equal(48.8, MetricsReader.ReadCpuTemperature(fixture.Path));
    }

    [Fact]
    public void ThermalZoneIsUsedWhenHwmonHasNoCpuSensor()
    {
        using var fixture = new TemporaryDirectory();
        var zone = Directory.CreateDirectory(Path.Combine(fixture.Path, "class", "thermal", "thermal_zone0")).FullName;
        File.WriteAllText(Path.Combine(zone, "type"), "x86_pkg_temp\n");
        File.WriteAllText(Path.Combine(zone, "temp"), "55321\n");

        Assert.Equal(55.3, MetricsReader.ReadCpuTemperature(fixture.Path));
    }

    [Fact]
    public void MissingOrUnrelatedTemperatureSensorsReturnNull()
    {
        using var fixture = new TemporaryDirectory();
        var zone = Directory.CreateDirectory(Path.Combine(fixture.Path, "class", "thermal", "thermal_zone0")).FullName;
        File.WriteAllText(Path.Combine(zone, "type"), "acpitz\n");
        File.WriteAllText(Path.Combine(zone, "temp"), "40000\n");

        Assert.Null(MetricsReader.ReadCpuTemperature(fixture.Path));
    }

    static void WriteSensor(string directory, int index, string label, string value)
    {
        File.WriteAllText(Path.Combine(directory, $"temp{index}_label"), label);
        File.WriteAllText(Path.Combine(directory, $"temp{index}_input"), value);
    }

    sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"a3mgr-metrics-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
