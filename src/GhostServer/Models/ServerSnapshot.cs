namespace GhostServer.Models;

public sealed class ServerSnapshot
{
    public string Hostname { get; init; } = "—";
    public string OperatingSystem { get; init; } = "—";
    public string Kernel { get; init; } = "—";
    public string Uptime { get; init; } = "—";
    public string Load { get; init; } = "—";
    public double CpuPercent { get; init; }
    public long MemoryUsedMb { get; init; }
    public long MemoryTotalMb { get; init; }
    public double DiskUsedGb { get; init; }
    public double DiskTotalGb { get; init; }
    public string Docker { get; init; } = "Not detected";
}

public sealed class ServiceStatus
{
    public string Name { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}

public sealed class ConnectionProbe
{
    public bool Connected { get; init; }
    public string? PresentedFingerprint { get; init; }
    public string? PresentedHostKeyAlgorithm { get; init; }
    public bool RequiresTrust { get; init; }
}
