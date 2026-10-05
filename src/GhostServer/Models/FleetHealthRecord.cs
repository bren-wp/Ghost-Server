using System.Globalization;

namespace GhostServer.Models;

public sealed class FleetHealthRecord
{
    public Guid ProfileId { get; init; }
    public DateTimeOffset RecordedUtc { get; init; }
    public string Status { get; init; } = string.Empty;
    public double CpuPercent { get; init; }
    public double MemoryPercent { get; init; }
    public double DiskPercent { get; init; }
    public string Load { get; init; } = "—";
    public string Message { get; init; } = string.Empty;

    public string RecordedLocal =>
        RecordedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string CpuLabel =>
        CpuPercent > 0
            ? CpuPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            : "—";

    public string MemoryLabel =>
        MemoryPercent > 0
            ? MemoryPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            : "—";

    public string DiskLabel =>
        DiskPercent > 0
            ? DiskPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            : "—";
}
