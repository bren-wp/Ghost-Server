namespace GhostServer.Models;

public sealed class AppSettings
{
    public int DashboardRefreshSeconds { get; set; } = 30;
    public string? DefaultBackupDirectory { get; set; }
    public Guid? LastSelectedServerId { get; set; }

    public void Normalize()
    {
        DashboardRefreshSeconds = DashboardRefreshSeconds switch
        {
            15 or 30 or 60 or 120 => DashboardRefreshSeconds,
            _ => 30
        };

        if (string.IsNullOrWhiteSpace(DefaultBackupDirectory))
        {
            DefaultBackupDirectory = null;
        }
    }
}
