param(
    [string]$PackageRoot,
    [string]$ScreenshotDirectory,
    [string]$ZipPath
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $PackageRoot) { $PackageRoot = Join-Path $repo 'artifacts/AllyBindings-win-x64' }
if (-not $ScreenshotDirectory) { $ScreenshotDirectory = Join-Path $repo 'artifacts/ui-screenshots' }
if (-not $ZipPath) { $ZipPath = "$PackageRoot.zip" }

$packageImages = Join-Path $PackageRoot 'docs/images'
if (-not (Test-Path -LiteralPath $packageImages -PathType Container)) {
    throw "Packaged documentation image directory was not found: $packageImages"
}

$imageNames = @(
    'ally-bindings-profiles.png',
    'ally-bindings-controller.png',
    'ally-bindings-shortcut.png',
    'ally-bindings-capture-update.png'
)
foreach ($imageName in $imageNames) {
    $source = Join-Path $ScreenshotDirectory $imageName
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Captured UI screenshot was not found: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $packageImages $imageName) -Force
}

if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
Compress-Archive -Path (Join-Path $PackageRoot '*') -DestinationPath $ZipPath -CompressionLevel Optimal
& (Join-Path $PSScriptRoot 'test-release-package.ps1') -PackageRoot $PackageRoot -ZipPath $ZipPath
Write-Output "Refreshed packaged documentation screenshots and rebuilt $ZipPath"
