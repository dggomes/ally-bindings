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
    & $dotnet test (Join-Path $repo 'tests/AllyBindings.Core.Tests/AllyBindings.Core.Tests.csproj') --configuration Release
    Assert-NativeSuccess 'core tests'
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
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') $publishDir
Copy-Item (Join-Path $repo 'LICENSE') $publishDir
Copy-Item (Join-Path $repo 'LICENSES/HidSharp-Apache-2.0.txt') $publishDir

$hashedFiles = @(
    'AllyBindings.HardwareValidator.exe',
    'RUNBOOK.md',
    'THIRD-PARTY-NOTICES.md',
    'LICENSE',
    'HidSharp-Apache-2.0.txt'
)
$checksumLines = foreach ($relative in $hashedFiles) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $publishDir $relative) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $relative"
}
[IO.File]::WriteAllLines((Join-Path $publishDir 'SHA256SUMS.txt'), $checksumLines, [Text.UTF8Encoding]::new($false))

Compress-Archive -Path "$publishDir/*" -DestinationPath $zipPath -CompressionLevel Optimal
& (Join-Path $repo 'scripts/test-hardware-validator-safety.ps1') -PackageRoot $publishDir -ZipPath $zipPath
Assert-NativeSuccess 'hardware validator safety validation'
Write-Output "Created private hardware validator: $zipPath"
