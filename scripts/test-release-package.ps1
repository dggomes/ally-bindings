param(
    [string]$PackageRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/AllyBindings-win-x64'),
    [string]$ZipPath = "$PackageRoot.zip",
    [long]$MaximumExecutableBytes = 200MB,
    [long]$MaximumZipBytes = 80MB
)

$ErrorActionPreference = 'Stop'

$required = @(
    'AllyBindings.exe',
    'README.md',
    'CHANGELOG.md',
    'SECURITY.md',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md',
    'CONTRIBUTING.md',
    'docs/ARCHITECTURE.md',
    'docs/HARDWARE-SPIKE.md',
    'docs/PLAN.md',
    'docs/FULL-VIRTUAL-CONTROLLER-VALIDATION.md',
    'docs/evidence/full-virtual-controller-release-approval.example.json',
    'docs/ARMOURY-TAP-SECURITY.md',
    'docs/ARMOURY-TAP-USER-GUIDE.md',
    'docs/images/ally-bindings-capture-update.png',
    'docs/images/ally-bindings-controller.png',
    'docs/images/ally-bindings-profiles.png',
    'docs/images/ally-bindings-shortcut.png',
    'LICENSES/HidSharp-Apache-2.0.txt',
    'LICENSES/TraceEvent-MIT.txt',
    'LICENSES/MinHook-BSD-2-Clause.txt',
    'LICENSES/ViGEm.NET-MIT.txt'
)
foreach ($relative in $required) {
    $path = Join-Path $PackageRoot $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Release package is missing required artifact: $relative"
    }
}

$expectedFiles = @($required | Sort-Object)
$actualFiles = @(Get-ChildItem -LiteralPath $PackageRoot -Recurse -File | ForEach-Object {
    [IO.Path]::GetRelativePath($PackageRoot, $_.FullName).Replace('\', '/')
} | Sort-Object)
$compositionDifference = @(Compare-Object -ReferenceObject $expectedFiles -DifferenceObject $actualFiles)
if ($compositionDifference.Count -ne 0) {
    throw "Release package directory does not match the exact allowlist: $($compositionDifference | Out-String)"
}

$unexpected = Get-ChildItem -LiteralPath $PackageRoot -Recurse -File | Where-Object {
    $_.Extension -eq '.pdb' -or $_.Name -match '(?i)usbpcap|wireshark'
}
if ($unexpected) {
    throw "Release package contains developer/capture dependency files: $($unexpected.FullName -join ', ')"
}

$exe = Get-Item -LiteralPath (Join-Path $PackageRoot 'AllyBindings.exe')
if ($exe.Length -gt $MaximumExecutableBytes) {
    throw "AllyBindings.exe is $($exe.Length) bytes, above the lean-install budget of $MaximumExecutableBytes bytes."
}
if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
    throw "Release ZIP is missing: $ZipPath"
}
$zip = Get-Item -LiteralPath $ZipPath
if ($zip.Length -gt $MaximumZipBytes) {
    throw "Release ZIP is $($zip.Length) bytes, above the lean-install budget of $MaximumZipBytes bytes."
}

$archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $zipFiles = @($archive.Entries | Where-Object { $_.Name } | ForEach-Object {
        $_.FullName.Replace('\', '/')
    } | Sort-Object)
    $duplicates = @($zipFiles | Group-Object | Where-Object Count -gt 1)
    $zipDifference = @(Compare-Object -ReferenceObject $expectedFiles -DifferenceObject $zipFiles)
    if ($duplicates.Count -ne 0 -or $zipDifference.Count -ne 0) {
        throw "Release ZIP does not match the exact allowlist or contains duplicate entries: $($zipDifference | Out-String)"
    }
}
finally {
    $archive.Dispose()
}

$notices = Get-Content -Raw -LiteralPath (Join-Path $PackageRoot 'THIRD-PARTY-NOTICES.md')
foreach ($dependency in @('HidSharp', 'Microsoft.Diagnostics.Tracing.TraceEvent', 'MinHook', 'Nefarius.ViGEm.Client')) {
    if ($notices.IndexOf($dependency, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Third-party notices do not identify $dependency."
    }
}

Write-Output "Release package validated: exe=$($exe.Length) bytes; zip=$($zip.Length) bytes; no PDB/USBPcap/Wireshark files."
