using Arma3Manager.Api.Infrastructure;
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
