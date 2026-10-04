using System.Text.Json;
using GhostServer.Models;

namespace GhostServer.Services;

public sealed class ProfileStore
{
    private readonly string _directory;
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ProfileStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GhostServer");
        _path = Path.Combine(_directory, "servers.json");
    }

    public async Task<IReadOnlyList<ServerProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<ServerProfile>();
        }

        await using var stream = File.OpenRead(_path);
        var profiles = await JsonSerializer.DeserializeAsync<List<ServerProfile>>(
            stream, JsonOptions, cancellationToken);
        return profiles ?? [];
    }

    public async Task SaveAsync(IEnumerable<ServerProfile> profiles, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);

        var temporary = _path + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, profiles, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporary, _path, true);
    }
}
