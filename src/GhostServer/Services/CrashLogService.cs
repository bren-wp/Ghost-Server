using System.IO;
using System.Globalization;
using System.Text;

namespace GhostServer.Services;

internal static class CrashLogService
{
    private const int MaxCrashLogs = 10;

    public static void Write(Exception exception, string source)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GhostServer",
                "Logs");

            Directory.CreateDirectory(directory);

            var path = Path.Combine(
                directory,
                $"crash-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.log");

            var version = typeof(CrashLogService).Assembly.GetName().Version?.ToString(3) ?? "unknown";

            var content = new StringBuilder()
                .AppendLine("Timestamp: " + DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture))
                .AppendLine("Source: " + source)
                .AppendLine("Ghost Server: " + version)
                .AppendLine("OS: " + Environment.OSVersion.VersionString)
                .AppendLine(".NET: " + Environment.Version.ToString())
                .AppendLine()
                .AppendLine(exception.ToString())
                .ToString();

            File.WriteAllText(path, content, Encoding.UTF8);
            TrimOldLogs(directory);
        }
        catch
        {
            // Crash logging must never replace the original failure.
        }
    }

    private static void TrimOldLogs(string directory)
    {
        var oldLogs = new DirectoryInfo(directory)
            .EnumerateFiles("crash-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.CreationTimeUtc)
            .Skip(MaxCrashLogs);

        foreach (var log in oldLogs)
        {
            try
            {
                log.Delete();
            }
            catch
            {
                // Best-effort retention cleanup.
            }
        }
    }
}
