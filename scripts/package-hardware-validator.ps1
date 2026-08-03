param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repo 'artifacts'
$publishDir = Join-Path $artifactRoot 'AllyBindings-HardwareValidator-win-x64'
$zipPath = "$publishDir.zip"
$project = Join-Path $repo 'src/AllyBindings.HardwareValidator/AllyBindings.HardwareValidator.csproj'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) {
    $dotnetCommand.Source
}
elseif (Test-Path -LiteralPath (Join-Path $HOME '.dotnet/dotnet') -PathType Leaf) {
    Join-Path $HOME '.dotnet/dotnet'
}
else {
    throw 'The .NET 8 SDK is required to package the hardware validator.'
}

function Assert-NativeSuccess([string]$Description) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

if (-not $SkipTests) {
    & $dotnet test (Join-Path $repo 'tests/AllyBindings.HardwareValidator.Tests/AllyBindings.HardwareValidator.Tests.csproj') --configuration Release
    Assert-NativeSuccess 'standalone hardware validator policy tests'
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
Assert-NativeSuccess 'hardware validator publish'

Get-ChildItem -LiteralPath $publishDir -Filter '*.pdb' -File | Remove-Item -Force
Copy-Item (Join-Path $repo 'lab/HARDWARE-VALIDATOR-RUNBOOK.md') (Join-Path $publishDir 'RUNBOOK.md')
Copy-Item (Join-Path $repo 'lab/HARDWARE-VALIDATOR-NOTICES.txt') (Join-Path $publishDir 'VALIDATOR-NOTICES.txt')
Copy-Item (Join-Path $repo 'lab/Verify-HardwareValidator-Package.ps1') (Join-Path $publishDir 'Verify-Package.ps1')
Copy-Item (Join-Path $repo 'lab/Build-HardwareValidator-Evidence.ps1') (Join-Path $publishDir 'Build-Evidence.ps1')
Copy-Item (Join-Path $repo 'LICENSE') $publishDir
New-Item -ItemType Directory -Path (Join-Path $publishDir 'LICENSES') -Force | Out-Null
$dotnetRoot = Split-Path -Parent $dotnet
Copy-Item (Join-Path $dotnetRoot 'LICENSE.txt') (Join-Path $publishDir 'LICENSES/dotnet-LICENSE.txt')
Copy-Item (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') (Join-Path $publishDir 'LICENSES/dotnet-ThirdPartyNotices.txt')

$hashedFiles = @(
    'AllyBindings.HardwareValidator.exe',
    'Build-Evidence.ps1',
    'LICENSE',
    'LICENSES/dotnet-LICENSE.txt',
    'LICENSES/dotnet-ThirdPartyNotices.txt',
    'RUNBOOK.md',
    'VALIDATOR-NOTICES.txt',
    'Verify-Package.ps1'
)
$checksumLines = foreach ($relative in $hashedFiles) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $publishDir $relative) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $relative"
}
[IO.File]::WriteAllLines((Join-Path $publishDir 'SHA256SUMS.txt'), $checksumLines, [Text.UTF8Encoding]::new($false))

Compress-Archive -Path "$publishDir/*" -DestinationPath $zipPath -CompressionLevel Optimal
& (Join-Path $repo 'scripts/test-hardware-validator-safety.ps1') -PackageRoot $publishDir -ZipPath $zipPath
Assert-NativeSuccess 'hardware validator safety assertions'
& (Join-Path $repo 'scripts/test-hardware-validator-evidence.ps1')
Assert-NativeSuccess 'hardware validator evidence sealing tests'
& (Join-Path $publishDir 'Verify-Package.ps1')
Write-Output "Created controlled lab hardware validator: $zipPath"
