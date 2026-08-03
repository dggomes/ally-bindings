using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllyBindings.Core;

public sealed record ProfileLoadResult(AppConfiguration Configuration, IReadOnlyList<string> Warnings);

public sealed class JsonProfileStore
{
    private readonly string _path;
    private readonly string _tapBarrierPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TapTeardownBarrier? _requiredTapTeardownBarrier;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonProfileStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _tapBarrierPath = $"{_path}.tap-barrier";
    }

    public string Path => _path;

    public async Task<ProfileLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await LoadTapBarrierSentinelAsync(cancellationToken);
        var backupPath = $"{_path}.bak";
        if (!File.Exists(_path))
        {
            if (File.Exists(backupPath))
            {
                try
                {
                    AppConfiguration? backup;
                    await using (var backupStream = File.OpenRead(backupPath))
                        backup = await JsonSerializer.DeserializeAsync<AppConfiguration>(backupStream, _jsonOptions, cancellationToken);
                    var recovered = ConfigurationValidator.Normalize(backup);
                    var uncertain = EnsureUncertainRecoveryIsBlocked(recovered.Configuration);
                    var effective = await ReconcileLoadedTapBarrierAsync(uncertain, cancellationToken);
                    File.Copy(backupPath, _path, overwrite: false);
                    if (effective != recovered.Configuration)
                        await SaveAsync(effective, cancellationToken);
                    return new(
                        effective,
                        recovered.Warnings.Concat(["The primary configuration was missing; the last valid backup was restored, but native tap writes require a Windows restart because a newer single-copy barrier may have been lost."]).ToArray());
                }
                catch (UnsupportedConfigurationVersionException)
                {
                    File.Copy(backupPath, _path, overwrite: false);
                    throw;
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    var blocked = CreateFailClosedRecoveryConfiguration();
                    var effective = await ReconcileLoadedTapBarrierAsync(blocked, cancellationToken);
                    await SaveAsync(effective, cancellationToken);
                    return new(effective, ["The primary configuration was missing and its backup was invalid; native tap writes require a Windows restart before use."]);
                }
            }
            var created = ApplyRequiredTapBarrier(AppConfiguration.CreateDefault());
            await SaveAsync(created, cancellationToken);
            return new(created, []);
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var parsed = await JsonSerializer.DeserializeAsync<AppConfiguration>(stream, _jsonOptions, cancellationToken);
            var normalized = ConfigurationValidator.Normalize(parsed);
            var effective = await ReconcileLoadedTapBarrierAsync(normalized.Configuration, cancellationToken);
            if (effective != normalized.Configuration)
                await SaveAsync(effective, cancellationToken);
            return new(effective, normalized.Warnings);
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

            if (File.Exists(backupPath))
            {
                try
                {
                    AppConfiguration? backup;
                    await using (var backupStream = File.OpenRead(backupPath))
                        backup = await JsonSerializer.DeserializeAsync<AppConfiguration>(backupStream, _jsonOptions, cancellationToken);
                    var recovered = ConfigurationValidator.Normalize(backup);
                    var uncertain = EnsureUncertainRecoveryIsBlocked(recovered.Configuration);
                    var backupEffective = await ReconcileLoadedTapBarrierAsync(uncertain, cancellationToken);
                    File.Copy(backupPath, _path, overwrite: true);
                    if (backupEffective != recovered.Configuration)
                        await SaveAsync(backupEffective, cancellationToken);
                    return new(
                        backupEffective,
                        recovered.Warnings.Concat(["The primary configuration was invalid; the last valid backup was restored, but native tap writes require a Windows restart because a newer single-copy barrier may have been lost."]).ToArray());
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

            var fallback = CreateFailClosedRecoveryConfiguration();
            var effective = await ReconcileLoadedTapBarrierAsync(fallback, cancellationToken);
            await SaveAsync(effective, cancellationToken);
            return new(effective, [$"Configuration was invalid and reset to Default; native tap writes require a Windows restart before use: {ex.Message}"]);
        }
    }

    public async Task SaveAsync(AppConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await SaveLockedAsync(ApplyRequiredTapBarrier(configuration), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ArmTapTeardownBarrierAsync(
        AppConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (configuration.ArmouryTapTeardownBlockedSinceUtc is not { } blockedSinceUtc)
            throw new ArgumentException("An Armoury tap teardown timestamp is required.", nameof(configuration));

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            _requiredTapTeardownBarrier = new(blockedSinceUtc, configuration.ArmouryTapTeardownBootIdentifier);
            var armed = ApplyRequiredTapBarrier(configuration);
            await WriteTapBarrierSentinelAsync(_requiredTapTeardownBarrier, cancellationToken);
            // The first replacement arms the primary; the second also arms the
            // last-valid backup before native injection is allowed to begin.
            await SaveLockedAsync(armed, cancellationToken);
            await SaveLockedAsync(armed, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task EstablishTapTeardownBootBaselineAsync(
        AppConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (configuration.ArmouryTapTeardownBlockedSinceUtc is not { } blockedSinceUtc ||
            configuration.ArmouryTapTeardownBootIdentifier is not { } bootIdentifier ||
            bootIdentifier == Guid.Empty)
            throw new ArgumentException("A blocked timestamp and non-empty boot identifier are required.", nameof(configuration));

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (_requiredTapTeardownBarrier is not { } current ||
                current.BlockedSinceUtc != blockedSinceUtc ||
                current.BootIdentifier is not null && current.BootIdentifier != bootIdentifier)
                throw new InvalidOperationException("The persisted tap barrier cannot accept this boot baseline.");

            var baseline = new TapTeardownBarrier(blockedSinceUtc, bootIdentifier);
            await WriteTapBarrierSentinelAsync(baseline, cancellationToken);
            _requiredTapTeardownBarrier = baseline;
            var updated = ApplyRequiredTapBarrier(configuration);
            await SaveLockedAsync(updated, cancellationToken);
            await SaveLockedAsync(updated, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ClearTapTeardownBarrierAsync(
        AppConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (configuration.ArmouryTapTeardownBlockedSinceUtc is not null ||
            configuration.ArmouryTapTeardownBootIdentifier is not null)
            throw new ArgumentException("The cleared Armoury tap teardown fields must both be null.", nameof(configuration));

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            // The sentinel is the commit marker. Clear both configuration copies
            // first; any exception or power loss leaves the sentinel armed. Only
            // its final deletion commits the teardown barrier release.
            var normalized = ConfigurationValidator.Normalize(configuration).Configuration;
            await WriteConfigurationFileAtomicallyAsync($"{_path}.bak", normalized, cancellationToken);
            await WriteConfigurationFileAtomicallyAsync(_path, normalized, cancellationToken);
            File.Delete(_tapBarrierPath);
            _requiredTapTeardownBarrier = null;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private AppConfiguration ApplyRequiredTapBarrier(AppConfiguration configuration) =>
        _requiredTapTeardownBarrier is { } barrier
            ? configuration with
            {
                ArmouryTapTeardownBlockedSinceUtc = barrier.BlockedSinceUtc,
                ArmouryTapTeardownBootIdentifier = barrier.BootIdentifier,
            }
            : configuration;

    private async Task LoadTapBarrierSentinelAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_tapBarrierPath)) return;
        try
        {
            await using var stream = File.OpenRead(_tapBarrierPath);
            var barrier = await JsonSerializer.DeserializeAsync<TapTeardownBarrier>(stream, _jsonOptions, cancellationToken);
            if (barrier is null || barrier.BlockedSinceUtc == default || barrier.BootIdentifier == Guid.Empty)
                throw new JsonException("The tap barrier sentinel is invalid.");
            _requiredTapTeardownBarrier = barrier;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged commit marker is evidence that release was not committed.
            _requiredTapTeardownBarrier = new(DateTimeOffset.UtcNow, null);
        }
    }

    private async Task<AppConfiguration> ReconcileLoadedTapBarrierAsync(
        AppConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (_requiredTapTeardownBarrier is null &&
            configuration.ArmouryTapTeardownBlockedSinceUtc is { } blockedSinceUtc)
        {
            _requiredTapTeardownBarrier = new(blockedSinceUtc, configuration.ArmouryTapTeardownBootIdentifier);
            await WriteTapBarrierSentinelAsync(_requiredTapTeardownBarrier, cancellationToken);
        }
        return ApplyRequiredTapBarrier(configuration);
    }

    private Task WriteTapBarrierSentinelAsync(
        TapTeardownBarrier barrier,
        CancellationToken cancellationToken) =>
        WriteJsonFileAtomicallyAsync(_tapBarrierPath, barrier, cancellationToken);

    private Task WriteConfigurationFileAtomicallyAsync(
        string path,
        AppConfiguration configuration,
        CancellationToken cancellationToken) =>
        WriteJsonFileAtomicallyAsync(path, configuration, cancellationToken);

    private async Task WriteJsonFileAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, value, _jsonOptions, cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private AppConfiguration EnsureUncertainRecoveryIsBlocked(AppConfiguration configuration)
    {
        if (_requiredTapTeardownBarrier is not null ||
            configuration.ArmouryTapTeardownBlockedSinceUtc is not null)
            return configuration;

        return configuration with
        {
            ArmouryTapTeardownBlockedSinceUtc = DateTimeOffset.UtcNow,
            ArmouryTapTeardownBootIdentifier = null,
        };
    }

    private static AppConfiguration CreateFailClosedRecoveryConfiguration() =>
        AppConfiguration.CreateDefault() with
        {
            ArmouryTapTeardownBlockedSinceUtc = DateTimeOffset.UtcNow,
            ArmouryTapTeardownBootIdentifier = null,
        };

    private async Task SaveLockedAsync(AppConfiguration configuration, CancellationToken cancellationToken)
    {
        var normalized = ConfigurationValidator.Normalize(configuration).Configuration;
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, normalized, _jsonOptions, cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
                File.Replace(tempPath, _path, $"{_path}.bak", ignoreMetadataErrors: true);
            else
                File.Move(tempPath, _path);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private sealed record TapTeardownBarrier(DateTimeOffset BlockedSinceUtc, Guid? BootIdentifier);
}
