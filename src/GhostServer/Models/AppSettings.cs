namespace GhostServer.Models;

public sealed class AppSettings
{
    public int DashboardRefreshSeconds { get; set; } = 30;
    public string? DefaultBackupDirectory { get; set; }
    public Guid? LastSelectedServerId { get; set; }
    public bool RememberWindowSize { get; set; } = true;
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

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

        WindowWidth = NormalizeWindowDimension(WindowWidth, 640, 2400);
        WindowHeight = NormalizeWindowDimension(WindowHeight, 440, 1600);

        if (!RememberWindowSize)
        {
            WindowWidth = null;
            WindowHeight = null;
        }
    }

    private static double? NormalizeWindowDimension(double? value, double minimum, double maximum)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        return Math.Clamp(value.Value, minimum, maximum);
    }
}
