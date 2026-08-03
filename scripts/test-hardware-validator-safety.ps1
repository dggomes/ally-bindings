param(
    [string]$PackageRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/AllyBindings-HardwareValidator-win-x64'),
    [string]$ZipPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/AllyBindings-HardwareValidator-win-x64.zip'),
    [long]$MaximumExecutableBytes = 115343360,
    [long]$MaximumZipBytes = 52428800
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$policy = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.HardwareValidator/HardwareLabPolicy.cs')
$program = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.HardwareValidator/Program.cs')
$writer = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.HardwareValidator/ExactRc73xaLabWriter.cs')
$project = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.HardwareValidator/AllyBindings.HardwareValidator.csproj')
$manifest = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.HardwareValidator/app.manifest')
$publicPackage = Get-Content -Raw -LiteralPath (Join-Path $repo 'scripts/package.ps1')

$goldenHex = '5AD102082C010200000000000000000001020000000000000000000101000000000000000000010100000000000000000000'
$goldenHash = 'fb0f2ac8167350edf147fb839be2306ccb15494c824a44badeff7aad083cf38b'
foreach ($required in @(
    'TargetVendorId = 0x0B05',
    'TargetProductId = 0x1B4C',
    'MaximumWireReportLength = 64',
    'write-m1-a-m2-b',
    'I SAVED SETTINGS; WRITE M1=A M2=B',
    $goldenHash,
    'IsApprovedProductName',
    'IsApprovedInterface'
)) {
    if ($policy.IndexOf($required, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Standalone fixed policy is missing: $required"
    }
}

foreach ($required in @(
    'Console.IsInputRedirected',
    'Console.ReadLine()',
    'ExactRc73xaLabWriter.WriteAsync',
    'ClaimOneShotAsync',
    'FileMode.CreateNew',
    'RECOVERY REQUIRED',
    'ArmouryRecoveryConfirmed: false'
)) {
    if ($program.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Validator safety flow is missing: $required"
    }
}
foreach ($required in @(
    'CreateFileW',
    'HidD_GetAttributes',
    'HidD_GetPreparsedData',
    'HidP_GetCaps',
    'HidD_SetFeature',
    'BuildWirePacket',
    'IsExactSystemIdentity()'
)) {
    if ($writer.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Same-handle writer is missing: $required"
    }
}
if ($writer -notmatch 'WriteAsync\s*\(\s*LabTargetSnapshot approvedTarget' -or
    $writer -match 'WriteAsync\s*\(\s*byte\[\]') {
    throw 'The sole write entry point must construct its fixed packet internally and accept no packet bytes.'
}
if ([regex]::Matches($writer, 'HidD_SetFeature\(').Count -ne 2) {
    throw 'Expected exactly one SET_FEATURE call site plus its single native declaration.'
}
foreach ($forbidden in @(
    'ProjectReference',
    'AsusRearButtonHidDevice.cs',
    'AllyBindings.Core'
)) {
    if ($project.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Standalone validator project contains forbidden dependency: $forbidden"
    }
}
foreach ($forbidden in @(
    'BuildMappingReport',
    'BuildNativeResetReport',
    'WriteFeatureReportAsync',
    'ReadFeatureReportAsync',
    'HidD_GetFeature',
    'GetFeature('
)) {
    if (($program + $writer + $policy).IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Standalone validator source contains forbidden general/read/reset capability: $forbidden"
    }
}
if ($manifest.IndexOf('level="requireAdministrator"', [StringComparison]::Ordinal) -lt 0) {
    throw 'The controlled lab validator must retain an explicit UAC boundary.'
}
if ($publicPackage.IndexOf('HardwareValidator', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'The public Ally Bindings package must not include the lab validator.'
}

$expectedAll = @(
    'AllyBindings.HardwareValidator.exe',
    'LICENSE',
    'LICENSES/HidSharp-Apache-2.0.txt',
    'RUNBOOK.md',
    'SHA256SUMS.txt',
    'THIRD-PARTY-NOTICES.md'
) | Sort-Object
$expectedHashed = @($expectedAll | Where-Object { $_ -ne 'SHA256SUMS.txt' })

function Get-RelativeFiles([string]$Root) {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File |
        ForEach-Object { [IO.Path]::GetFullPath($_.FullName).Substring($rootFull.Length).Replace([IO.Path]::DirectorySeparatorChar, '/') } |
        Sort-Object)
}

function Assert-Package([string]$Root) {
    $actual = Get-RelativeFiles $Root
    if (@(Compare-Object -ReferenceObject $expectedAll -DifferenceObject $actual).Count -ne 0) {
        throw "Validator package does not match the exact allowlist: $($actual -join ', ')"
    }

    $lines = @(Get-Content -LiteralPath (Join-Path $Root 'SHA256SUMS.txt'))
    $entries = foreach ($line in $lines) {
        if ($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9._/-]+)$') { throw "Malformed checksum line: $line" }
        if ($Matches[2].Contains('..') -or $Matches[2].StartsWith('/') -or $Matches[2].Contains('\')) {
            throw "Unsafe checksum path: $($Matches[2])"
        }
        [pscustomobject]@{ Hash = $Matches[1]; Name = $Matches[2] }
    }
    if (@($entries | Group-Object Name | Where-Object Count -ne 1).Count -ne 0) {
        throw 'SHA256SUMS contains duplicate payload names.'
    }
    $actualNames = @($entries.Name | Sort-Object)
    if (@(Compare-Object -ReferenceObject $expectedHashed -DifferenceObject $actualNames).Count -ne 0) {
        throw 'SHA256SUMS does not cover every payload exactly once.'
    }
    foreach ($entry in $entries) {
        $actualHash = (Get-FileHash -LiteralPath (Join-Path $Root $entry.Name) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $entry.Hash) { throw "Checksum mismatch for $($entry.Name)." }
    }
}

Assert-Package $PackageRoot
if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) { throw "Validator ZIP is missing: $ZipPath" }
$executable = Get-Item -LiteralPath (Join-Path $PackageRoot 'AllyBindings.HardwareValidator.exe')
$zip = Get-Item -LiteralPath $ZipPath
if ($executable.Length -gt $MaximumExecutableBytes) { throw "Validator executable exceeds size budget: $($executable.Length) bytes." }
if ($zip.Length -gt $MaximumZipBytes) { throw "Validator ZIP exceeds size budget: $($zip.Length) bytes." }

$binaryText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($executable.FullName))
foreach ($required in @('ExactRc73xaLabWriter', 'HidD_SetFeature')) {
    if ($binaryText.IndexOf($required, [StringComparison]::Ordinal) -lt 0) { throw "Compiled validator is missing: $required" }
}
foreach ($forbidden in @('BuildMappingReport', 'BuildNativeResetReport', 'WriteFeatureReportAsync', 'ReadFeatureReportAsync')) {
    if ($binaryText.IndexOf($forbidden, [StringComparison]::Ordinal) -ge 0) { throw "Compiled validator contains forbidden symbol: $forbidden" }
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ("ally-validator-" + [Guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $temp
    Assert-Package $temp
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}

Write-Output "Controlled one-shot hardware validator source, binary, ZIP, and checksum assertions passed."
