using System.IO;
using GhostServer.Models;

namespace GhostServer.Services;

public sealed class FleetHistoryStore
{
    private const int MaxRecordsPerProfile = 100;
    private const int MaxTotalRecords = 2000;

    private readonly string _path;

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
        var records = await JsonFileStore.LoadAsync<List<FleetHealthRecord>>(
            _path,
            cancellationToken) ?? [];

        return Normalize(records);
    }

    public Task SaveAsync(
        IEnumerable<FleetHealthRecord> records,
        CancellationToken cancellationToken = default)
    {
        return JsonFileStore.SaveAsync(
            _path,
            Normalize(records),
            cancellationToken);
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
}
