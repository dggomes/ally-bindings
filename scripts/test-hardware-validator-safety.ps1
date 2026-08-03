param(
    [string]$PackageRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/AllyBindings-HardwareValidator-win-x64'),
    [string]$ZipPath = "$PackageRoot.zip",
    [long]$MaximumExecutableBytes = 110MB,
    [long]$MaximumZipBytes = 50MB
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$corePolicy = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.Core/AsusRearButtonProtocol.cs')
$labPolicy = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.Core/AsusRearButtonLabValidation.cs')
$labTests = Get-Content -Raw -LiteralPath (Join-Path $repo 'tests/AllyBindings.Core.Tests/AsusRearButtonLabValidationTests.cs')
$program = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.HardwareValidator/Program.cs')
$project = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.HardwareValidator/AllyBindings.HardwareValidator.csproj')
$manifest = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.HardwareValidator/app.manifest')
$device = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.Windows/AsusRearButtonHidDevice.cs')
$publicPackage = Get-Content -Raw -LiteralPath (Join-Path $repo 'scripts/package.ps1')

foreach ($required in @(
    'public static bool CustomWritesApproved => false',
    'public static bool RecoveryWritesApproved => false'
)) {
    if ($corePolicy.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The public application hardware lock is missing: $required"
    }
}

foreach ($required in @(
    'WriteCommand = "write-m1-a-m2-b"',
    'ConfirmationPhrase = "I SAVED SETTINGS; WRITE M1=A M2=B"',
    'BuildMappingReport(ControllerButton.A, ControllerButton.B)',
    'inputRedirected',
    'compatibleInterfaceCount != 1'
)) {
    if ($labPolicy.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The one-shot lab policy is missing: $required"
    }
}

foreach ($pinnedPacketFact in @(
    '5AD102082C010200000000000000000001020000000000000000000101000000000000000000010100000000000000000000',
    'fb0f2ac8167350edf147fb839be2306ccb15494c824a44badeff7aad083cf38b'
)) {
    if ($labTests.IndexOf($pinnedPacketFact, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "The fixed lab packet is not pinned by exact vector and hash: $pinnedPacketFact"
    }
}

foreach ($required in @(
    'AsusRearButtonLabValidation.BuildOneShotReport()',
    'Console.IsInputRedirected',
    'Console.ReadLine()',
    'device.GetSnapshotInterfaceIdentityKeys().Count',
    'device.WriteFeatureReportAsync(report',
    'LabAuditStore.ClaimOneShotAsync',
    'FileMode.CreateNew',
    '"one-shot-claimed.json"',
    'The validator has no reset command',
    'ArmouryRecoveryConfirmed: false'
)) {
    if ($program.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The validator safety flow is missing: $required"
    }
}

foreach ($forbidden in @(
    'BuildNativeResetReport',
    'ControllerButton.X',
    'ControllerButton.Y',
    'RestoreDefaultAsync',
    '--force',
    '--yes'
)) {
    if ($program.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "The fixed validator contains a forbidden general/recovery path: $forbidden"
    }
}

if ($project.IndexOf('ProjectReference Include="..\AllyBindings.Windows', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
    $project.IndexOf('AsusRearButtonHidDevice.cs', [StringComparison]::Ordinal) -lt 0) {
    throw 'The validator must link only the narrow HID adapter, not reference the full WPF application.'
}
if ($manifest.IndexOf('level="requireAdministrator"', [StringComparison]::Ordinal) -lt 0) {
    throw 'The lab validator must retain an explicit UAC boundary.'
}
if ($device.IndexOf('expectedInterfaceIdentityKeys', [StringComparison]::Ordinal) -lt 0 -or
    $device.IndexOf('IsExactInterfaceSnapshot', [StringComparison]::Ordinal) -lt 0) {
    throw 'The HID write does not revalidate the exact inspected interface set.'
}
if ($publicPackage.IndexOf('HardwareValidator', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'The normal Ally Bindings package must not include the private hardware validator.'
}

$expected = @(
    'AllyBindings.HardwareValidator.exe',
    'LICENSE',
    'LICENSES/HidSharp-Apache-2.0.txt',
    'RUNBOOK.md',
    'SHA256SUMS.txt',
    'THIRD-PARTY-NOTICES.md'
) | Sort-Object
$packageRootFull = [IO.Path]::GetFullPath($PackageRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$actual = Get-ChildItem -LiteralPath $PackageRoot -Recurse -File |
    ForEach-Object {
        [IO.Path]::GetFullPath($_.FullName).Substring($packageRootFull.Length).Replace([IO.Path]::DirectorySeparatorChar, '/')
    } |
    Sort-Object
$difference = @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual)
if ($difference.Count -ne 0) {
    throw "Private validator package does not match its exact allowlist: $($difference | Out-String)"
}

$checksums = Get-Content -LiteralPath (Join-Path $PackageRoot 'SHA256SUMS.txt')
if ($checksums.Count -ne 5) {
    throw 'SHA256SUMS.txt must cover every package payload except itself.'
}
foreach ($line in $checksums) {
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') {
        throw "Malformed checksum line: $line"
    }
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $PackageRoot $Matches[2]) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $Matches[1]) {
        throw "Checksum mismatch for $($Matches[2])."
    }
}

if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
    throw "Private validator ZIP is missing: $ZipPath"
}
$executable = Get-Item -LiteralPath (Join-Path $PackageRoot 'AllyBindings.HardwareValidator.exe')
if ($executable.Length -gt $MaximumExecutableBytes) {
    throw "Private validator executable exceeds its $MaximumExecutableBytes-byte budget."
}
$zip = Get-Item -LiteralPath $ZipPath
if ($zip.Length -gt $MaximumZipBytes) {
    throw "Private validator ZIP exceeds its $MaximumZipBytes-byte budget."
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $zipFiles = @($archive.Entries | Where-Object Name | Select-Object -ExpandProperty FullName | Sort-Object)
    $zipDifference = @(Compare-Object -ReferenceObject $expected -DifferenceObject $zipFiles)
    if ($zipDifference.Count -ne 0) {
        throw "Private validator ZIP does not match its exact allowlist: $($zipDifference | Out-String)"
    }
}
finally {
    $archive.Dispose()
}

Write-Output "Private one-shot hardware validator safety and package assertions passed: exe=$($executable.Length), zip=$($zip.Length)."
