namespace GhostServer.Models;

public sealed class DatabaseEngineStatus
{
    public string Engine { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string ServiceStatus { get; init; } = string.Empty;
    public string Access { get; init; } = string.Empty;
    public List<string> Databases { get; } = [];

    public int DatabaseCount => Databases.Count;

    public string DatabaseNames =>
        Databases.Count == 0
            ? "No database names available."
            : string.Join(Environment.NewLine, Databases);
}
