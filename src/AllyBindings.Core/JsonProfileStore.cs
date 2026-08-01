using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllyBindings.Core;

public sealed record ProfileLoadResult(AppConfiguration Configuration, IReadOnlyList<string> Warnings);

public sealed class JsonProfileStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonProfileStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public string Path => _path;

    public async Task<ProfileLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            var created = AppConfiguration.CreateDefault();
            await SaveAsync(created, cancellationToken);
            return new(created, []);
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var parsed = await JsonSerializer.DeserializeAsync<AppConfiguration>(stream, _jsonOptions, cancellationToken);
            var normalized = ConfigurationValidator.Normalize(parsed);
            return new(normalized.Configuration, normalized.Warnings);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var stamp = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            var corruptPath = $"{_path}.corrupt-{stamp}";
            try
            {
                File.Move(_path, corruptPath, overwrite: false);
            }
            catch (IOException)
            {
                // Keep recovering even when preserving the primary fails.
            }

            var backupPath = $"{_path}.bak";
            if (File.Exists(backupPath))
            {
                try
                {
                    await using var backupStream = File.OpenRead(backupPath);
                    var backup = await JsonSerializer.DeserializeAsync<AppConfiguration>(backupStream, _jsonOptions, cancellationToken);
                    var recovered = ConfigurationValidator.Normalize(backup);
                    File.Copy(backupPath, _path, overwrite: true);
                    return new(
                        recovered.Configuration,
                        recovered.Warnings.Concat(["The primary configuration was invalid; the last valid backup was restored."]).ToArray());
                }
                catch (UnsupportedConfigurationVersionException)
                {
                    File.Copy(backupPath, _path, overwrite: true);
                    throw;
                }
                catch (Exception backupEx) when (backupEx is JsonException or IOException or UnauthorizedAccessException)
                {
                    // Fall through to a safe Default config; both damaged files remain available for diagnostics.
                }
            }

            var fallback = AppConfiguration.CreateDefault();
            await SaveAsync(fallback, cancellationToken);
            return new(fallback, [$"Configuration was invalid and reset to Default: {ex.Message}"]);
        }
    }

    public async Task SaveAsync(AppConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var normalized = ConfigurationValidator.Normalize(configuration).Configuration;
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, normalized, _jsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                if (File.Exists(_path))
                {
                    File.Replace(tempPath, _path, $"{_path}.bak", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, _path);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
