namespace GhostServer.Models;

public sealed class FleetServerStatus
{
    public Guid ProfileId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Authentication { get; init; } = string.Empty;
    public string Trust { get; init; } = string.Empty;
    public string Health { get; set; } = "Not checked";
    public string OperatingSystem { get; set; } = "—";
    public string Uptime { get; set; } = "—";
    public string Load { get; set; } = "—";
    public string Cpu { get; set; } = "—";
    public string Memory { get; set; } = "—";
    public string LastConnected { get; init; } = "Never";
}
