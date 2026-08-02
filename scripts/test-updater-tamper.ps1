param(
    [Parameter(Mandatory=$true)][string]$PackageRoot,
    [Parameter(Mandatory=$true)][string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$source = [IO.File]::ReadAllText((Join-Path $RepositoryRoot 'src/AllyBindings.Windows/GitHubUpdateService.cs'))
$startMarker = 'private const string InstallerScript = """'
$start = $source.IndexOf($startMarker, [StringComparison]::Ordinal)
if ($start -lt 0) { throw 'Installer script start marker was not found.' }
$start += $startMarker.Length
$end = $source.IndexOf('"""' + ';', $start, [StringComparison]::Ordinal)
if ($end -lt 0) { throw 'Installer script end marker was not found.' }
$installer = $source.Substring($start, $end - $start).TrimStart("`r", "`n")

$testId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "ally-bindings-updater-tamper-$testId"
$updateRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "AllyBindings/updates/tamper-test-$testId"
$staging = Join-Path $updateRoot 'staging'
$destination = Join-Path $testRoot 'installed'
$configPath = Join-Path $testRoot 'config/config.json'
$installerPath = Join-Path $updateRoot 'install-update.ps1'

try {
    New-Item -ItemType Directory -Force -Path $staging, $destination, (Split-Path -Parent $configPath) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PackageRoot 'AllyBindings.exe') -Destination (Join-Path $staging 'AllyBindings.exe')
    $expectedHash = (Get-FileHash (Join-Path $staging 'AllyBindings.exe') -Algorithm SHA256).Hash
    [IO.File]::WriteAllBytes((Join-Path $staging 'AllyBindings.exe'), [byte[]](1,2,3,4))
    Copy-Item -LiteralPath "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -Destination (Join-Path $destination 'AllyBindings.exe')
    $oldHash = (Get-FileHash (Join-Path $destination 'AllyBindings.exe') -Algorithm SHA256).Hash
    [IO.File]::WriteAllText($configPath, '{"schemaVersion":1,"sentinel":"unchanged"}')
    [IO.File]::WriteAllText($installerPath, $installer)

    $oldProcess = Start-Process -FilePath "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" `
        -ArgumentList '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 1' -PassThru
    $installerProcess = Start-Process -FilePath "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" `
        -ArgumentList @(
            '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $installerPath,
            '-ProcessId', $oldProcess.Id, '-PackageRoot', $staging, '-ExecutableSha256', $expectedHash,
            '-Destination', $destination, '-UpdateRoot', $updateRoot, '-ConfigPath', $configPath, '-NonInteractive'
        ) -Wait -PassThru

    if ($installerProcess.ExitCode -ne 1) {
        throw "Tampered staged executable was not rejected; installer exit code was $($installerProcess.ExitCode)."
    }
    if ((Get-FileHash (Join-Path $destination 'AllyBindings.exe') -Algorithm SHA256).Hash -ne $oldHash) {
        throw 'Tampered staged executable changed the installed application.'
    }
    if ([IO.File]::ReadAllText($configPath) -ne '{"schemaVersion":1,"sentinel":"unchanged"}') {
        throw 'Tampered staged executable changed the configuration.'
    }
    Write-Output 'Updater tamper test passed: post-verification staged executable mutation was rejected before replacement.'
}
finally {
    Get-Process -Name AllyBindings -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $updateRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
