param(
    [Parameter(Mandatory=$true)][string]$PackageRoot,
    [Parameter(Mandatory=$true)][string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$sourcePath = Join-Path $RepositoryRoot 'src/AllyBindings.Windows/GitHubUpdateService.cs'
$source = [IO.File]::ReadAllText($sourcePath)
$startMarker = 'private const string InstallerScript = """'
$start = $source.IndexOf($startMarker, [StringComparison]::Ordinal)
if ($start -lt 0) { throw 'Installer script start marker was not found.' }
$start += $startMarker.Length
$endMarker = '"""' + ';'
$end = $source.IndexOf($endMarker, $start, [StringComparison]::Ordinal)
if ($end -lt 0) { throw 'Installer script end marker was not found.' }
$installer = $source.Substring($start, $end - $start).TrimStart("`r", "`n")

$testId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "ally-bindings-updater-test-$testId"
$managedUpdatesRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'AllyBindings/updates'
$updateRoot = Join-Path $managedUpdatesRoot "integration-test-$testId"
$staging = Join-Path $updateRoot 'staging'
$destination = Join-Path $testRoot 'installed'
$installerPath = Join-Path $updateRoot 'install-update.ps1'
$configPath = Join-Path $testRoot 'config/config.json'

try {
    New-Item -ItemType Directory -Force -Path $staging, $destination | Out-Null
    Copy-Item -Path (Join-Path $PackageRoot '*') -Destination $staging -Recurse -Force
    [IO.File]::WriteAllText($installerPath, $installer)
    [IO.File]::WriteAllText((Join-Path $destination 'preexisting.txt'), 'preserve me')
    [IO.File]::WriteAllText((Join-Path $destination 'CHANGELOG.md'), 'old changelog that must be atomically replaced')
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $configPath) | Out-Null
    [IO.File]::WriteAllText($configPath, '{"schemaVersion":1,"sentinel":"preserve me"}')

    $oldProcess = Start-Process -FilePath "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" `
        -ArgumentList '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 1' -PassThru

    & $installerPath -ProcessId $oldProcess.Id -PackageRoot $staging -Destination $destination -UpdateRoot $updateRoot -ConfigPath $configPath -NonInteractive

    if (-not (Test-Path -LiteralPath (Join-Path $destination 'AllyBindings.exe') -PathType Leaf)) {
        throw 'Updater integration test did not install AllyBindings.exe.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $destination 'preexisting.txt') -PathType Leaf)) {
        throw 'Updater integration test removed an unrelated existing file.'
    }
    if ([IO.File]::ReadAllText((Join-Path $destination 'CHANGELOG.md')) -eq 'old changelog that must be atomically replaced') {
        throw 'Updater integration test did not replace an existing package file.'
    }
    if (Test-Path -LiteralPath $updateRoot) {
        throw 'Updater integration test did not remove its verified update staging directory.'
    }
    if ([IO.File]::ReadAllText($configPath) -ne '{"schemaVersion":1,"sentinel":"preserve me"}') {
        throw 'Updater success path unexpectedly modified the supplied configuration snapshot.'
    }

    Write-Output 'Updater integration test passed: replacement, startup handshake, configuration preservation, and cleanup completed.'
}
finally {
    Get-Process -Name AllyBindings -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $updateRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
