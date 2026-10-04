namespace GhostServer.Models;

public sealed class ScheduledTaskStatus
{
    public string Name { get; init; } = string.Empty;
    public string TimerUnit { get; init; } = string.Empty;
    public string Schedule { get; init; } = string.Empty;
    public string NextRun { get; init; } = "—";
    public string State { get; init; } = "unknown";
    public bool Enabled { get; init; }

    public string EnabledLabel => Enabled ? "Enabled" : "Disabled";
}
