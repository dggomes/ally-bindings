$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [IO.Path]::GetFullPath($PSScriptRoot)
$expectedPayload = @(
    'AllyBindings.HardwareValidator.exe',
    'LICENSE',
    'LICENSES/dotnet-LICENSE.txt',
    'LICENSES/dotnet-ThirdPartyNotices.txt',
    'RUNBOOK.md',
    'VALIDATOR-NOTICES.txt',
    'Verify-Package.ps1'
) | Sort-Object
$expectedAll = @($expectedPayload + 'SHA256SUMS.txt') | Sort-Object

$actual = @(Get-ChildItem -LiteralPath $root -File -Recurse | ForEach-Object {
    if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Reparse points are forbidden: $($_.FullName)"
    }
    [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
}) | Sort-Object
if ([string]::Join("`n", $actual) -cne [string]::Join("`n", $expectedAll)) {
    throw "Package allowlist mismatch.`nExpected:`n$($expectedAll -join "`n")`nActual:`n$($actual -join "`n")"
}

$lines = @(Get-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt'))
if ($lines.Count -ne $expectedPayload.Count) {
    throw "Expected exactly $($expectedPayload.Count) checksum entries; found $($lines.Count)."
}

$entries = foreach ($line in $lines) {
    if ($line -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9._/-]+)$') {
        throw "Malformed checksum entry: $line"
    }
    $name = $Matches[2]
    if ([IO.Path]::IsPathRooted($name) -or $name.Contains('..') -or $name.Contains('\')) {
        throw "Unsafe checksum path: $name"
    }
    [pscustomobject]@{ Hash = $Matches[1]; Name = $name }
}

$names = @($entries.Name | Sort-Object)
if (($entries | Group-Object Name | Where-Object Count -ne 1) -or
    [string]::Join("`n", $names) -cne [string]::Join("`n", $expectedPayload)) {
    throw 'Checksum entries are duplicated, missing, or outside the exact allowlist.'
}

foreach ($entry in $entries) {
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $root $entry.Name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $entry.Hash) {
        throw "Checksum mismatch for $($entry.Name)."
    }
}

$exeHash = ($entries | Where-Object Name -ceq 'AllyBindings.HardwareValidator.exe').Hash
Write-Output 'CONTROLLED VALIDATOR PACKAGE VALID'
Write-Output "Executable SHA-256: $exeHash"
