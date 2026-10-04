namespace GhostServer.Models;

public sealed class DockerContainerStatus
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}
