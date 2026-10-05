using System.IO;
using GhostServer.Models;

namespace GhostServer.Services;

public sealed class FleetHistoryStore : IDisposable
{
    private const int MaxRecordsPerProfile = 100;
    private const int MaxTotalRecords = 2000;

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FleetHistoryStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GhostServer");

        _path = Path.Combine(directory, "fleet-history.json");
    }

    public async Task<IReadOnlyList<FleetHealthRecord>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await JsonFileStore.LoadAsync<List<FleetHealthRecord>>(
                _path,
                cancellationToken) ?? [];

            return Normalize(records);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        IEnumerable<FleetHealthRecord> records,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(records);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await JsonFileStore.SaveAsync(
                _path,
                normalized,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<FleetHealthRecord> Normalize(
        IEnumerable<FleetHealthRecord> records)
    {
        var normalized = records
            .Where(record =>
                record.ProfileId != Guid.Empty &&
                record.RecordedUtc != default &&
                !string.IsNullOrWhiteSpace(record.Status))
            .GroupBy(record => record.ProfileId)
            .SelectMany(group => group
                .OrderByDescending(record => record.RecordedUtc)
                .Take(MaxRecordsPerProfile))
            .OrderByDescending(record => record.RecordedUtc)
            .Take(MaxTotalRecords)
            .ToList();

        return normalized;
    }

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
