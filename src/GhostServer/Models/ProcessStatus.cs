namespace GhostServer.Models;

public sealed class ProcessStatus
{
    public int Pid { get; init; }
    public string User { get; init; } = string.Empty;
    public double CpuPercent { get; init; }
    public double MemoryPercent { get; init; }
    public string Elapsed { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
}
