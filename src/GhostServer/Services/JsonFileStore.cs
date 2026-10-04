using System.IO;
using System.Text.Json;

namespace GhostServer.Services;

internal static class JsonFileStore
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static async Task<T?> LoadAsync<T>(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            return await DeserializeAsync<T>(path, cancellationToken);
        }
        catch (JsonException)
        {
            var backup = path + ".bak";
            if (!File.Exists(backup))
            {
                throw;
            }

            return await DeserializeAsync<T>(backup, cancellationToken);
        }
    }

    public static async Task SaveAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("A storage directory is required.");
        }

        Directory.CreateDirectory(directory);

        var temporary = path + ".tmp";
        var backup = path + ".bak";

        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    Options,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(path))
            {
                File.Copy(path, backup, true);
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static async Task<T?> LoadExternalAsync<T>(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("JSON file was not found.", path);
        }

        return await DeserializeAsync<T>(path, cancellationToken);
    }

    public static Task SaveExternalAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("A destination directory is required.");
        }

        Directory.CreateDirectory(directory);
        return SerializeExternalAsync(path, value, cancellationToken);
    }

    private static async Task<T?> DeserializeAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await JsonSerializer.DeserializeAsync<T>(
            stream,
            Options,
            cancellationToken);
    }

    private static async Task SerializeExternalAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);

        await JsonSerializer.SerializeAsync(
            stream,
            value,
            Options,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
