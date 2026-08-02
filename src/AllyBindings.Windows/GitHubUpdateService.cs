using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using AllyBindings.Core;

namespace AllyBindings.Windows;

public sealed class GitHubUpdateService : IDisposable
{
    private const long MaximumDownloadBytes = 256L * 1024 * 1024;
    public const string ReleaseRepository = "dggomes/ally-bindings";
    private const string ReleasesApi = "https://api.github.com/repos/" + ReleaseRepository + "/releases?per_page=10";
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public GitHubUpdateService(HttpMessageHandler? handler = null)
    {
        CleanupAbandonedDownloads();
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AllyBindings", CurrentVersion.ToString(3)));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentSemanticVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+', 2)[0]
        ?? CurrentVersion.ToString(3);

    public async Task<UpdateCandidate?> CheckAsync(bool includePrerelease, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var response = await _httpClient.GetAsync(ReleasesApi, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken) ?? [];
        return GitHubReleaseUpdateSelector.Select(releases, CurrentSemanticVersion, includePrerelease, ReleaseRepository);
    }

    public async Task<PreparedUpdate> DownloadAndPrepareAsync(
        UpdateCandidate candidate,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AllyBindings",
            "updates",
            $"{candidate.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(updateRoot);
        var zipPath = Path.Combine(updateRoot, "update.zip");
        var stagingPath = Path.Combine(updateRoot, "staging");

        try
        {
            using var response = await _httpClient.GetAsync(candidate.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            if (total > MaximumDownloadBytes)
            {
                throw new InvalidDataException("The update download is unexpectedly large.");
            }
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    if (copied > MaximumDownloadBytes)
                    {
                        throw new InvalidDataException("The update download exceeded the safety limit.");
                    }
                    if (total > 0) progress?.Report((double)copied / total.Value);
                }
                await destination.FlushAsync(cancellationToken);
            }

            var package = UpdatePackageStager.VerifyExtractAndDescribe(zipPath, stagingPath, candidate.Sha256);
            return new PreparedUpdate(candidate, updateRoot, package.PackageRoot, package.ExecutableSha256);
        }
        catch
        {
            TryDeleteDirectory(updateRoot);
            throw;
        }
    }

    public static void LaunchInstaller(PreparedUpdate update, string destinationDirectory, int processId)
    {
        ArgumentNullException.ThrowIfNull(update);
        destinationDirectory = Path.GetFullPath(destinationDirectory);
        EnsureNoReparsePoints(update.UpdateRoot, "update working directory");
        EnsureNoReparsePoints(update.PackageRoot, "verified package directory");
        EnsureNoReparsePoints(destinationDirectory, "application directory");
        EnsureWritable(destinationDirectory);

        var scriptPath = Path.Combine(update.UpdateRoot, "install-update.ps1");
        File.WriteAllText(scriptPath, InstallerScript);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-ProcessId");
        startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-PackageRoot");
        startInfo.ArgumentList.Add(update.PackageRoot);
        startInfo.ArgumentList.Add("-ExecutableSha256");
        startInfo.ArgumentList.Add(update.ExecutableSha256);
        startInfo.ArgumentList.Add("-Destination");
        startInfo.ArgumentList.Add(destinationDirectory);
        startInfo.ArgumentList.Add("-UpdateRoot");
        startInfo.ArgumentList.Add(update.UpdateRoot);
        startInfo.ArgumentList.Add("-ConfigPath");
        startInfo.ArgumentList.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AllyBindings",
            "config.json"));

        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("Windows could not start the update installer.");
        }
    }

    private static void EnsureWritable(string directory)
    {
        var probe = Path.Combine(directory, $".ally-bindings-write-test-{Guid.NewGuid():N}");
        var incoming = $"{probe}.new";
        try
        {
            File.WriteAllText(probe, "old");
            File.WriteAllText(incoming, "new");
            File.Replace(incoming, probe, destinationBackupFileName: null);
            if (!string.Equals(File.ReadAllText(probe), "new", StringComparison.Ordinal))
            {
                throw new IOException("The atomic replacement probe returned unexpected content.");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            throw new InvalidOperationException(
                "The app folder does not support safe atomic replacement. Move Ally Bindings to a normal user-owned NTFS folder before updating.", ex);
        }
        finally
        {
            try { File.Delete(incoming); } catch { }
            try { File.Delete(probe); } catch { }
        }
    }

    private static void EnsureNoReparsePoints(string path, string description)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"The {description} cannot pass through a symbolic link or junction: {current.FullName}");
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private static void CleanupAbandonedDownloads()
    {
        var updatesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AllyBindings",
            "updates");
        if (!Directory.Exists(updatesRoot)) return;

        foreach (var directory in Directory.EnumerateDirectories(updatesRoot))
        {
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                    File.Exists(Path.Combine(directory, "install-update.ps1")) ||
                    Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddMinutes(-1))
                {
                    continue;
                }
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best effort. A future launch retries stale incomplete-download cleanup.
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
    }

    private const string InstallerScript = """
param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$PackageRoot,
    [Parameter(Mandatory=$true)][string]$ExecutableSha256,
    [Parameter(Mandatory=$true)][string]$Destination,
    [Parameter(Mandatory=$true)][string]$UpdateRoot,
    [Parameter(Mandatory=$true)][string]$ConfigPath,
    [switch]$NonInteractive
)
$ErrorActionPreference = 'Stop'
if ($ExecutableSha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw 'The expected executable SHA-256 digest is invalid.'
}
$backup = Join-Path $UpdateRoot 'backup'
$configBackup = Join-Path $UpdateRoot 'config-backup.json'
$transactionId = [Guid]::NewGuid().ToString('N')
$copied = New-Object System.Collections.Generic.List[string]
$temporaries = New-Object System.Collections.Generic.List[string]
$configSnapshotTaken = $false
$configExisted = $false
$safeToRelaunch = $false
try {
    $oldProcess = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -ne $oldProcess -and -not $oldProcess.WaitForExit(30000)) {
        throw 'The running application did not exit within 30 seconds; no update files were installed.'
    }
    $safeToRelaunch = $true
    if (Test-Path -LiteralPath $ConfigPath -PathType Container) {
        throw 'The application configuration path unexpectedly refers to a directory.'
    }
    if ((Test-Path -LiteralPath $ConfigPath) -and ((Get-Item -LiteralPath $ConfigPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The application configuration path cannot be a symbolic link or junction.'
    }
    $configExisted = Test-Path -LiteralPath $ConfigPath -PathType Leaf
    if ($configExisted) {
        Copy-Item -LiteralPath $ConfigPath -Destination $configBackup -Force
    }
    $configSnapshotTaken = $true
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    $relative = 'AllyBindings.exe'
    $packageExecutable = Join-Path $PackageRoot $relative
    if (-not (Test-Path -LiteralPath $packageExecutable -PathType Leaf)) {
        throw 'The verified update package does not contain AllyBindings.exe.'
    }
    if (((Get-Item -LiteralPath $packageExecutable -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The staged update executable cannot be a symbolic link or junction.'
    }
    $packageStream = [IO.File]::Open($packageExecutable, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $hasher = [Security.Cryptography.SHA256]::Create()
        try {
            $actualExecutableSha256 = ([BitConverter]::ToString($hasher.ComputeHash($packageStream))).Replace('-', '').ToLowerInvariant()
        } finally {
            $hasher.Dispose()
        }
        if (-not $actualExecutableSha256.Equals($ExecutableSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The staged update executable changed after package verification.'
        }
        $packageStream.Position = 0
    } catch {
        $packageStream.Dispose()
        throw
    }
    $target = Join-Path $Destination $relative
    $saved = Join-Path $backup $relative
    $incoming = "$target.allybindings-$transactionId-new"
    $displaced = "$target.allybindings-$transactionId-displaced"
    if (Test-Path -LiteralPath $target -PathType Container) {
        throw 'Refusing to replace an AllyBindings.exe directory with the update executable.'
    }
    if ((Test-Path -LiteralPath $target) -and ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The installed executable cannot be a symbolic link or junction.'
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $temporaries.Add($incoming)
    try {
        $incomingStream = [IO.File]::Open($incoming, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $packageStream.CopyTo($incomingStream)
            $incomingStream.Flush($true)
        } finally {
            $incomingStream.Dispose()
        }
    } finally {
        $packageStream.Dispose()
    }
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        Copy-Item -LiteralPath $target -Destination $saved -Force
    }
    $copied.Add($relative)
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        try {
            [IO.File]::Replace($incoming, $target, $displaced, $true)
        } finally {
            Remove-Item -LiteralPath $displaced -Force -ErrorAction SilentlyContinue
        }
    } else {
        [IO.File]::Move($incoming, $target)
    }
    $marker = Join-Path $UpdateRoot 'startup-ok'
    Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue
    $env:ALLY_BINDINGS_UPDATE_SUCCESS_MARKER = $marker
    $updatedProcess = Start-Process -FilePath (Join-Path $Destination 'AllyBindings.exe') -ArgumentList '--updated' -PassThru
    Remove-Item Env:ALLY_BINDINGS_UPDATE_SUCCESS_MARKER -ErrorAction SilentlyContinue
    $healthy = $false
    for ($attempt = 0; $attempt -lt 150; $attempt++) {
        if (Test-Path -LiteralPath $marker -PathType Leaf) {
            $healthy = $true
            break
        }
        if ($updatedProcess.HasExited) {
            throw 'The updated application exited before confirming successful initialization.'
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $healthy) {
        Stop-Process -Id $updatedProcess.Id -Force -ErrorAction SilentlyContinue
        throw 'The updated application did not confirm successful initialization within 15 seconds.'
    }
    Remove-Item -LiteralPath $UpdateRoot -Recurse -Force -ErrorAction SilentlyContinue
}
catch {
    $message = $_.Exception.Message
    $rollbackErrors = New-Object System.Collections.Generic.List[string]
    Remove-Item Env:ALLY_BINDINGS_UPDATE_SUCCESS_MARKER -ErrorAction SilentlyContinue

    try {
        if ($null -ne $updatedProcess -and -not $updatedProcess.HasExited) {
            Stop-Process -Id $updatedProcess.Id -Force -ErrorAction Stop
            if (-not $updatedProcess.WaitForExit(5000)) {
                $rollbackErrors.Add('The failed updated process did not exit within five seconds.')
            }
        }
    } catch {
        $rollbackErrors.Add("Could not stop the failed updated process: $($_.Exception.Message)")
    }

    foreach ($incoming in $temporaries) {
        Remove-Item -LiteralPath $incoming -Force -ErrorAction SilentlyContinue
    }

    $rollbackItems = $copied.ToArray()
    [Array]::Reverse($rollbackItems)
    foreach ($relative in $rollbackItems) {
        $target = Join-Path $Destination $relative
        $saved = Join-Path $backup $relative
        $restoreIncoming = "$target.allybindings-$transactionId-restore"
        $failedVersion = "$target.allybindings-$transactionId-failed-version"
        try {
            Remove-Item -LiteralPath $incoming -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $restoreIncoming -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $failedVersion -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $saved -PathType Leaf) {
                Copy-Item -LiteralPath $saved -Destination $restoreIncoming -Force
                if (Test-Path -LiteralPath $target -PathType Leaf) {
                    [IO.File]::Replace($restoreIncoming, $target, $failedVersion, $true)
                    Remove-Item -LiteralPath $failedVersion -Force -ErrorAction SilentlyContinue
                } else {
                    [IO.File]::Move($restoreIncoming, $target)
                }
            } elseif (Test-Path -LiteralPath $target) {
                Remove-Item -LiteralPath $target -Force -ErrorAction Stop
            }
        } catch {
            $rollbackErrors.Add("Could not restore application file '$relative': $($_.Exception.Message)")
        } finally {
            Remove-Item -LiteralPath $restoreIncoming -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $failedVersion -Force -ErrorAction SilentlyContinue
        }
    }

    if ($configSnapshotTaken) {
        $configIncoming = "$ConfigPath.allybindings-$transactionId-restore"
        $failedConfig = "$ConfigPath.allybindings-$transactionId-failed-version"
        try {
            Remove-Item -LiteralPath $configIncoming -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $failedConfig -Force -ErrorAction SilentlyContinue
            if ($configExisted) {
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ConfigPath) | Out-Null
                Copy-Item -LiteralPath $configBackup -Destination $configIncoming -Force
                if (Test-Path -LiteralPath $ConfigPath -PathType Leaf) {
                    [IO.File]::Replace($configIncoming, $ConfigPath, $failedConfig, $true)
                    Remove-Item -LiteralPath $failedConfig -Force -ErrorAction SilentlyContinue
                } else {
                    [IO.File]::Move($configIncoming, $ConfigPath)
                }
            } elseif (Test-Path -LiteralPath $ConfigPath) {
                Remove-Item -LiteralPath $ConfigPath -Force -ErrorAction Stop
            }
        } catch {
            $rollbackErrors.Add("Could not restore the previous configuration: $($_.Exception.Message)")
        } finally {
            Remove-Item -LiteralPath $configIncoming -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $failedConfig -Force -ErrorAction SilentlyContinue
        }
    }

    if ($safeToRelaunch -and $rollbackErrors.Count -eq 0) {
        try {
            Start-Process -FilePath (Join-Path $Destination 'AllyBindings.exe')
        } catch {
            $rollbackErrors.Add("Could not relaunch the previous application: $($_.Exception.Message)")
        }
    }

    $rollbackSummary = if ($rollbackErrors.Count -eq 0) {
        'Existing application files and configuration were restored.'
    } else {
        "Rollback was incomplete:`n - " + ($rollbackErrors -join "`n - ")
    }
    if (-not $NonInteractive) {
        try {
            Add-Type -AssemblyName PresentationFramework
            [System.Windows.MessageBox]::Show(
                "Ally Bindings update failed.`n`n$message`n`n$rollbackSummary",
                'Ally Bindings updater') | Out-Null
        } catch {
            [Diagnostics.Debug]::WriteLine("Updater error dialog could not be shown: $($_.Exception.Message)")
        }
    }
    if ($rollbackErrors.Count -gt 0) {
        Write-Error $rollbackSummary -ErrorAction Continue
    }
    exit 1
}
""";
}

public sealed record PreparedUpdate(
    UpdateCandidate Candidate,
    string UpdateRoot,
    string PackageRoot,
    string ExecutableSha256);
