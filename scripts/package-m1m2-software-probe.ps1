param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repo 'artifacts'
$publishDir = Join-Path $artifactRoot 'AllyBindings-M1M2-SoftwareProbe-win-x64'
$zipPath = "$publishDir.zip"
$project = Join-Path $repo 'src/AllyBindings.M1M2Probe/AllyBindings.M1M2Probe.csproj'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) {
    $dotnetCommand.Source
}
elseif (Test-Path -LiteralPath (Join-Path $HOME '.dotnet/dotnet') -PathType Leaf) {
    Join-Path $HOME '.dotnet/dotnet'
}
else {
    throw 'The .NET 8 SDK is required to package the M1/M2 software probe.'
}

function Assert-NativeSuccess([string]$Description) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

$probeSources = @(
    Get-ChildItem -LiteralPath (Join-Path $repo 'src/AllyBindings.M1M2Probe') -Filter '*.cs' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' }
    Get-ChildItem -LiteralPath (Join-Path $repo 'src/AllyBindings.SoftwareProbe.Core') -Filter '*.cs' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' }
)
$forbidden = @(
    'HidD_SetFeature',
    'WriteFeatureReport',
    'HidSharp',
    'CreateFileW',
    'SetupDiGetClassDevs',
    'HidHide.*\b(?:enable|disable|cloak|hide)\b',
    '(?:sc\.exe|New-Service|CreateService)'
)
foreach ($pattern in $forbidden) {
    $matches = @($probeSources | Select-String -Pattern $pattern -CaseSensitive:$false)
    if ($matches.Count -ne 0) {
        throw "Forbidden software-probe source capability matched '$pattern'."
    }
}

if (-not $SkipTests) {
    & $dotnet test (Join-Path $repo 'tests/AllyBindings.Core.Tests/AllyBindings.Core.Tests.csproj') --configuration Release
    Assert-NativeSuccess 'core and evidence tests'
}

if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

& $dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $publishDir
Assert-NativeSuccess 'M1/M2 software probe publish'

Get-ChildItem -LiteralPath $publishDir -Filter '*.pdb' -File | Remove-Item -Force
$publishedExecutable = Join-Path $publishDir 'AllyBindings.M1M2Probe.exe'
if ((Get-Item -LiteralPath $publishedExecutable).Length -gt 100MB) {
    throw 'Published software-probe executable exceeds the 100 MB safety budget.'
}
$binaryText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($publishedExecutable))
foreach ($required in @('CreateXbox360Controller', 'F17F18KeyboardHook', 'SoftwareProbeSession')) {
    if ($binaryText.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Published software probe is missing expected symbol: $required"
    }
}
foreach ($forbiddenSymbol in @(
    'AllyBindings.Core.dll',
    'HidD_SetFeature',
    'WriteFeatureReport',
    'write-m1-a-m2-b',
    'HidSharp',
    'CreateServiceW'
)) {
    if ($binaryText.IndexOf($forbiddenSymbol, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Published software probe contains forbidden symbol: $forbiddenSymbol"
    }
}

Copy-Item (Join-Path $repo 'lab/M1M2-SOFTWARE-PROBE-RUNBOOK.md') (Join-Path $publishDir 'RUNBOOK.md')
Copy-Item (Join-Path $repo 'lab/M1M2-SOFTWARE-PROBE-NOTICES.txt') (Join-Path $publishDir 'NOTICES.txt')
Copy-Item (Join-Path $repo 'lab/Run-M1M2-Software-Probe.ps1') (Join-Path $publishDir 'Run-Software-Probe.ps1')
Copy-Item (Join-Path $repo 'lab/Verify-M1M2-Software-Probe-Package.ps1') (Join-Path $publishDir 'Verify-Package.ps1')
Copy-Item (Join-Path $repo 'LICENSE') $publishDir
New-Item -ItemType Directory -Path (Join-Path $publishDir 'LICENSES') -Force | Out-Null
$dotnetRoot = Split-Path -Parent $dotnet
Copy-Item (Join-Path $dotnetRoot 'LICENSE.txt') (Join-Path $publishDir 'LICENSES/dotnet-LICENSE.txt')
Copy-Item (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') (Join-Path $publishDir 'LICENSES/dotnet-ThirdPartyNotices.txt')

$hashedFiles = @(
    'AllyBindings.M1M2Probe.exe',
    'LICENSE',
    'LICENSES/dotnet-LICENSE.txt',
    'LICENSES/dotnet-ThirdPartyNotices.txt',
    'NOTICES.txt',
    'RUNBOOK.md',
    'Run-Software-Probe.ps1',
    'Verify-Package.ps1'
)
$checksumLines = foreach ($relative in $hashedFiles) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $publishDir $relative) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $relative"
}
[IO.File]::WriteAllLines((Join-Path $publishDir 'SHA256SUMS.txt'), $checksumLines, [Text.UTF8Encoding]::new($false))

& (Join-Path $publishDir 'Verify-Package.ps1')
Assert-NativeSuccess 'package verification'
Compress-Archive -Path "$publishDir/*" -DestinationPath $zipPath -CompressionLevel Optimal
if ((Get-Item -LiteralPath $zipPath).Length -gt 120MB) {
    throw 'Compressed software-probe package exceeds the 120 MB safety budget.'
}
$outerHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zipPath.sha256", "$outerHash  $([IO.Path]::GetFileName($zipPath))`n", [Text.UTF8Encoding]::new($false))

$verificationRoot = Join-Path ([IO.Path]::GetTempPath()) ("ally-bindings-package-verify-" + [Guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $zipPath -DestinationPath $verificationRoot
    & (Join-Path $verificationRoot 'Verify-Package.ps1')
    $verifiedOuterHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sidecar = Get-Content -LiteralPath "$zipPath.sha256" -Raw
    if ($sidecar -cnotmatch "^$verifiedOuterHash  $([regex]::Escape([IO.Path]::GetFileName($zipPath)))`r?`n?$") {
        throw 'Outer ZIP checksum sidecar does not match the compressed artifact.'
    }
}
finally {
    if (Test-Path -LiteralPath $verificationRoot) {
        Remove-Item -LiteralPath $verificationRoot -Recurse -Force
    }
}
Write-Output "Created M1/M2 software probe: $zipPath"
Write-Output "SHA-256: $outerHash"
