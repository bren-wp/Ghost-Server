using System.IO;
using GhostServer.Models;

namespace GhostServer.Services;

public sealed class ProfileStore : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProfileStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GhostServer");

        _path = Path.Combine(directory, "servers.json");
    }

    public async Task<IReadOnlyList<ServerProfile>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = await JsonFileStore.LoadAsync<List<ServerProfile>>(
                _path,
                cancellationToken) ?? [];

            return NormalizeAndValidate(profiles);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        IEnumerable<ServerProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        var validated = NormalizeAndValidate(profiles);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await JsonFileStore.SaveAsync(
                _path,
                validated,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static Task ExportAsync(
        string destinationPath,
        IEnumerable<ServerProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        var validated = NormalizeAndValidate(profiles);
        return JsonFileStore.SaveExternalAsync(
            destinationPath,
            validated,
            cancellationToken);
    }

    public static async Task<IReadOnlyList<ServerProfile>> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var imported = await JsonFileStore.LoadExternalAsync<List<ServerProfile>>(
            sourcePath,
            cancellationToken) ?? [];

        return NormalizeAndValidate(imported);
    }

    private static List<ServerProfile> NormalizeAndValidate(
        IEnumerable<ServerProfile> profiles)
    {
        var result = new List<ServerProfile>();
        var seenIds = new HashSet<Guid>();

        foreach (var source in profiles)
        {
            var profile = new ServerProfile
            {
                Id = source.Id == Guid.Empty ? Guid.NewGuid() : source.Id,
                Name = source.Name?.Trim() ?? string.Empty,
                Host = source.Host?.Trim() ?? string.Empty,
                Port = source.Port,
                Username = source.Username?.Trim() ?? string.Empty,
                Authentication = string.Equals(
                    source.Authentication,
                    "PrivateKey",
                    StringComparison.OrdinalIgnoreCase)
                    ? "PrivateKey"
                    : "Password",
                PrivateKeyPath = string.IsNullOrWhiteSpace(source.PrivateKeyPath)
                    ? null
                    : source.PrivateKeyPath.Trim(),
                HostKeyFingerprint = string.IsNullOrWhiteSpace(source.HostKeyFingerprint)
                    ? null
                    : source.HostKeyFingerprint.Trim(),
                LastConnectedUtc = source.LastConnectedUtc
            };

            if (string.IsNullOrWhiteSpace(profile.Name) ||
                string.IsNullOrWhiteSpace(profile.Host) ||
                string.IsNullOrWhiteSpace(profile.Username) ||
                profile.Port is < 1 or > 65535)
            {
                throw new InvalidDataException(
                    "A server profile contains an invalid name, host, username or port.");
            }

            if (profile.Authentication == "PrivateKey" &&
                string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
            {
                throw new InvalidDataException(
                    $"Private-key profile '{profile.Name}' does not contain a key path.");
            }

            if (!seenIds.Add(profile.Id))
            {
                profile.Id = Guid.NewGuid();
                seenIds.Add(profile.Id);
            }

            result.Add(profile);
        }

        return result;
    }

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
