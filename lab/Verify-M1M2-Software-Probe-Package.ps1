$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$expectedPayload = @(
    'AllyBindings.M1M2Probe.exe',
    'LICENSE',
    'LICENSES/dotnet-LICENSE.txt',
    'LICENSES/dotnet-ThirdPartyNotices.txt',
    'NOTICES.txt',
    'RUNBOOK.md',
    'Run-Software-Probe.ps1',
    'Verify-Package.ps1'
) | Sort-Object
$expectedAll = @($expectedPayload + 'SHA256SUMS.txt') | Sort-Object

$actual = @(Get-ChildItem -LiteralPath $root -File -Recurse | ForEach-Object {
    if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Reparse points are forbidden: $($_.FullName)"
    }
    $_.FullName.Substring($root.Length).TrimStart([char[]]@(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )).Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
}) | Sort-Object

if (@(Compare-Object $expectedAll $actual).Count -ne 0) {
    throw "Package allowlist mismatch.`nExpected:`n$($expectedAll -join "`n")`nActual:`n$($actual -join "`n")"
}

$packageFiles = @(Get-ChildItem -LiteralPath $root -File -Recurse)
$expandedBytes = ($packageFiles | Measure-Object -Property Length -Sum).Sum
if ($expandedBytes -gt 120MB) {
    throw 'Expanded package exceeds the 120 MB safety budget.'
}
if ((Get-Item -LiteralPath (Join-Path $root 'AllyBindings.M1M2Probe.exe')).Length -gt 100MB) {
    throw 'Probe executable exceeds the 100 MB safety budget.'
}
foreach ($textFile in $packageFiles | Where-Object { $_.Extension -in @('.md', '.txt', '.ps1') }) {
    if ($textFile.Length -gt 2MB) {
        throw "Text payload exceeds the 2 MB safety budget: $($textFile.Name)"
    }
}

$lines = @(Get-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt'))
if ($lines.Count -ne $expectedPayload.Count) {
    throw "Expected exactly $($expectedPayload.Count) checksum entries; found $($lines.Count)."
}

$entries = @()
foreach ($line in $lines) {
    if ($line -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9._/-]+)$') {
        throw "Malformed checksum line: $line"
    }
    $name = $Matches[2]
    if ($name.Contains('..') -or $name.StartsWith('/') -or $name.Contains('\')) {
        throw "Unsafe checksum path: $name"
    }
    $entries += [pscustomobject]@{ Hash = $Matches[1]; Name = $name }
}

if (@(Compare-Object $expectedPayload @($entries.Name | Sort-Object)).Count -ne 0) {
    throw 'Checksum manifest file list does not match the package allowlist.'
}

foreach ($entry in $entries) {
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $root $entry.Name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $entry.Hash) {
        throw "Checksum mismatch for $($entry.Name)."
    }
}

$executableBytes = [IO.File]::ReadAllBytes((Join-Path $root 'AllyBindings.M1M2Probe.exe'))
$executableText = [Text.Encoding]::UTF8.GetString($executableBytes) + [Text.Encoding]::Unicode.GetString($executableBytes)
foreach ($requiredSymbol in @('CreateXbox360Controller', 'F11F12KeyboardHook', 'SoftwareProbeSession')) {
    if ($executableText.IndexOf($requiredSymbol, [StringComparison]::Ordinal) -lt 0) {
        throw "Probe executable is missing expected software-only symbol: $requiredSymbol"
    }
}
foreach ($forbiddenSymbol in @('AllyBindings.Core.dll', 'HidD_SetFeature', 'WriteFeatureReport', 'SendInput', 'write-m1-a-m2-b', 'HidSharp', 'CreateServiceW')) {
    if ($executableText.IndexOf($forbiddenSymbol, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Probe executable contains forbidden capability symbol: $forbiddenSymbol"
    }
}

$runbook = Get-Content -LiteralPath (Join-Path $root 'RUNBOOK.md') -Raw
$notices = Get-Content -LiteralPath (Join-Path $root 'NOTICES.txt') -Raw
if ($runbook -notmatch 'never opens an ASUS HID interface' -or
    $notices -notmatch 'no ASUS HID write API' -or
    $notices -notmatch 'never installs') {
    throw 'Required safety disclosures are missing.'
}

Write-Output 'PACKAGE VERIFIED'
Write-Output 'Software-only: no ASUS HID writes, no driver installation, no device hiding.'
