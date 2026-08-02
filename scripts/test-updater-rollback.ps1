param(
    [Parameter(Mandatory=$true)][string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
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
if ($installer -notmatch 'if \(\$safeToRelaunch -and \$rollbackErrors\.Count -eq 0\)') {
    throw 'Installer can relaunch after an incomplete rollback.'
}
if ($installer -match 'Get-ChildItem\s+-LiteralPath\s+\$PackageRoot') {
    throw 'Installer still performs a multi-file package transaction instead of replacing only AllyBindings.exe.'
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "ally-bindings-rollback-test-$([Guid]::NewGuid().ToString('N'))"
$updateRoot = Join-Path $testRoot 'update'
$staging = Join-Path $updateRoot 'staging'
$destination = Join-Path $testRoot 'installed'
$configPath = Join-Path $testRoot 'config/config.json'
$relaunchMarker = Join-Path $testRoot 'old-app-relaunched'
$installerPath = Join-Path $updateRoot 'install-update.ps1'
$oldBuild = Join-Path $testRoot 'old-build'
$newBuild = Join-Path $testRoot 'new-build'
$oldExe = Join-Path $oldBuild 'AllyBindings.exe'
$newExe = Join-Path $newBuild 'AllyBindings.exe'
$oldConfig = '{"schemaVersion":1,"sentinel":"old-compatible-config"}'

$oldSource = @'
using System;
using System.IO;
using System.Threading;
public static class OldApp
{
    public static void Main()
    {
        var marker = Environment.GetEnvironmentVariable("ALLY_BINDINGS_TEST_RELAUNCH_MARKER");
        if (!String.IsNullOrWhiteSpace(marker)) File.WriteAllText(marker, "relaunched");
        Thread.Sleep(500);
    }
}
'@
$newSource = @'
using System;
using System.IO;
public static class BrokenNewApp
{
    public static void Main()
    {
        var config = Environment.GetEnvironmentVariable("ALLY_BINDINGS_TEST_CONFIG_PATH");
        if (!String.IsNullOrWhiteSpace(config)) File.WriteAllText(config, "{\"schemaVersion\":2,\"sentinel\":\"migrated-by-failed-update\"}");
        Environment.Exit(23);
    }
}
'@

try {
    New-Item -ItemType Directory -Force -Path $staging, $destination, (Split-Path -Parent $configPath), $oldBuild, $newBuild | Out-Null
    Add-Type -TypeDefinition $oldSource -Language CSharp -OutputAssembly $oldExe -OutputType ConsoleApplication
    Add-Type -TypeDefinition $newSource -Language CSharp -OutputAssembly $newExe -OutputType ConsoleApplication

    Copy-Item -LiteralPath $oldExe -Destination (Join-Path $destination 'AllyBindings.exe')
    Copy-Item -LiteralPath $newExe -Destination (Join-Path $staging 'AllyBindings.exe')
    $newExeHash = (Get-FileHash (Join-Path $staging 'AllyBindings.exe') -Algorithm SHA256).Hash
    [IO.File]::WriteAllText((Join-Path $destination 'stable.txt'), 'old file')
    [IO.File]::WriteAllText((Join-Path $staging 'stable.txt'), 'new file')
    [IO.File]::WriteAllText($configPath, $oldConfig)
    [IO.File]::WriteAllText($installerPath, $installer)
    $oldExeHash = (Get-FileHash (Join-Path $destination 'AllyBindings.exe') -Algorithm SHA256).Hash

    $oldProcess = Start-Process -FilePath "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" `
        -ArgumentList '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 1' -PassThru

    $env:ALLY_BINDINGS_TEST_CONFIG_PATH = $configPath
    $env:ALLY_BINDINGS_TEST_RELAUNCH_MARKER = $relaunchMarker
    $installerProcess = Start-Process -FilePath "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" `
        -ArgumentList @(
            '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $installerPath,
            '-ProcessId', $oldProcess.Id, '-PackageRoot', $staging, '-ExecutableSha256', $newExeHash, '-Destination', $destination,
            '-UpdateRoot', $updateRoot, '-ConfigPath', $configPath, '-NonInteractive'
        ) -Wait -PassThru
    Remove-Item Env:ALLY_BINDINGS_TEST_CONFIG_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:ALLY_BINDINGS_TEST_RELAUNCH_MARKER -ErrorAction SilentlyContinue

    if ($installerProcess.ExitCode -ne 1) {
        throw "Expected failed update installer exit code 1, got $($installerProcess.ExitCode)."
    }
    if ([IO.File]::ReadAllText($configPath) -ne $oldConfig) {
        throw 'Rollback did not restore the schema-1 configuration after the failed new app wrote schema 2.'
    }
    if ([IO.File]::ReadAllText((Join-Path $destination 'stable.txt')) -ne 'old file') {
        throw 'Rollback did not restore an existing application file.'
    }
    $restoredExeHash = (Get-FileHash (Join-Path $destination 'AllyBindings.exe') -Algorithm SHA256).Hash
    if ($restoredExeHash -ne $oldExeHash) {
        throw 'Rollback did not restore the previous executable.'
    }

    for ($attempt = 0; $attempt -lt 50 -and -not (Test-Path -LiteralPath $relaunchMarker); $attempt++) {
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $relaunchMarker -PathType Leaf)) {
        throw 'Rollback did not relaunch the previous application.'
    }

    Write-Output 'Updater rollback test passed: binary files, schema-compatible configuration, and old-app relaunch were restored.'
}
finally {
    Remove-Item Env:ALLY_BINDINGS_TEST_CONFIG_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:ALLY_BINDINGS_TEST_RELAUNCH_MARKER -ErrorAction SilentlyContinue
    Get-Process -Name AllyBindings -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
