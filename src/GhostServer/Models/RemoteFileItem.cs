namespace GhostServer.Models;

public sealed class RemoteFileItem
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public DateTime LastWriteTime { get; init; }

    public string TypeLabel => IsDirectory ? "Folder" : "File";

    public string SizeLabel
    {
        get
        {
            if (IsDirectory)
            {
                return "—";
            }

            var size = (double)SizeBytes;
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return unit == 0 ? $"{SizeBytes:N0} B" : $"{size:0.##} {units[unit]}";
        }
    }
}
