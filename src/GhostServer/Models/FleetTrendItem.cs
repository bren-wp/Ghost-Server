using System.Globalization;

namespace GhostServer.Models;

public sealed class FleetTrendItem
{
    public Guid ProfileId { get; init; }
    public string ServerName { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public int HealthyCount { get; init; }
    public int AttentionCount { get; init; }
    public int FailureCount { get; init; }
    public double AverageCpuPercent { get; init; }
    public double MaxCpuPercent { get; init; }
    public double AverageMemoryPercent { get; init; }
    public double MaxMemoryPercent { get; init; }
    public double AverageDiskPercent { get; init; }
    public double MaxDiskPercent { get; init; }
    public string LatestStatus { get; init; } = "No data";

    public string AverageCpuLabel => FormatPercent(AverageCpuPercent);
    public string MaxCpuLabel => FormatPercent(MaxCpuPercent);
    public string AverageMemoryLabel => FormatPercent(AverageMemoryPercent);
    public string MaxMemoryLabel => FormatPercent(MaxMemoryPercent);
    public string AverageDiskLabel => FormatPercent(AverageDiskPercent);
    public string MaxDiskLabel => FormatPercent(MaxDiskPercent);

    private static string FormatPercent(double value) =>
        value > 0
            ? value.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            : "—";
}
