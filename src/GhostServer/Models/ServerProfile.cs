namespace GhostServer.Models;

public sealed class ServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string Authentication { get; set; } = "Password";
    public string? PrivateKeyPath { get; set; }
    public string? HostKeyFingerprint { get; set; }
    public DateTimeOffset? LastConnectedUtc { get; set; }

    public string Endpoint => $"{Host}:{Port}";
}
