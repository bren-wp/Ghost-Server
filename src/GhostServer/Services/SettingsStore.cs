using System.IO;
using GhostServer.Models;

namespace GhostServer.Services;

public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GhostServer");

        _path = Path.Combine(directory, "settings.json");
    }

    public async Task<AppSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await JsonFileStore.LoadAsync<AppSettings>(
            _path,
            cancellationToken) ?? new AppSettings();

        settings.Normalize();
        return settings;
    }

    public async Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        settings.Normalize();
        await JsonFileStore.SaveAsync(
            _path,
            settings,
            cancellationToken);
    }
}
