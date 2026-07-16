using System.Globalization;
using System.Text;
using Arma3Manager.Api.Contracts;

namespace Arma3Manager.Api.Infrastructure;

public static class MetricsCsv
{
    public static string Create(IEnumerable<MetricsSample> samples)
    {
        var csv = new StringBuilder();
        csv.AppendLine("timestamp_utc,cpu_usage_percent,cpu_cores_used,cpu_load_percent,cores_capacity,memory_used_mb,memory_percent,active_players,active_headless_clients");
        foreach (var sample in samples)
        {
            csv.Append(sample.SampledAt.UtcDateTime.ToString("O")).Append(',')
                .Append(Number(sample.CpuUsagePercent)).Append(',')
                .Append(sample.CpuCoresUsed?.ToString("F3", CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(Number(sample.CpuPercent)).Append(',')
                .Append(sample.CoresCapacity.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append((sample.MemoryUsedBytes / 1024d / 1024d).ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                .Append(sample.MemoryPercent.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(sample.ActivePlayers?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(sample.ActiveHeadlessClients?.ToString(CultureInfo.InvariantCulture) ?? "").AppendLine();
        }
        return csv.ToString();
    }

    static string Number(double? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
}
