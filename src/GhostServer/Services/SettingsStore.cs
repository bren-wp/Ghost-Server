using System.IO;
using GhostServer.Models;

namespace GhostServer.Services;

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = await JsonFileStore.LoadAsync<AppSettings>(
                _path,
                cancellationToken) ?? new AppSettings();

            settings.Normalize();
            return settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            settings.Normalize();
            await JsonFileStore.SaveAsync(
                _path,
                settings,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
