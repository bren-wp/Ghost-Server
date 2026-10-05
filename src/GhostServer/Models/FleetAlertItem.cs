namespace GhostServer.Models;

public sealed class FleetAlertItem
{
    public required FleetHealthRecord Record { get; init; }
    public string ServerName { get; init; } = string.Empty;

    public Guid ProfileId => Record.ProfileId;
    public string RecordedLocal => Record.RecordedLocal;
    public string Status => Record.Status;
    public string Acknowledgement => Record.AcknowledgementLabel;
    public string Cpu => Record.CpuLabel;
    public string Memory => Record.MemoryLabel;
    public string Disk => Record.DiskLabel;
    public string Load => Record.Load;
    public string Details => Record.Message;
}
