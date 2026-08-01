param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repo "artifacts"
$publishDir = Join-Path $artifactRoot "AllyBindings-win-x64"
$zipPath = "$publishDir.zip"

function Assert-NativeSuccess([string]$Description) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

if (-not $SkipTests) {
    dotnet test (Join-Path $repo "AllyBindings.sln") --configuration Release
    Assert-NativeSuccess "dotnet test"
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

dotnet publish (Join-Path $repo "src/AllyBindings.Windows/AllyBindings.Windows.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $publishDir
Assert-NativeSuccess "dotnet publish"

Copy-Item (Join-Path $repo "README.md") $publishDir
Copy-Item (Join-Path $repo "docs/HARDWARE-SPIKE.md") $publishDir
Copy-Item (Join-Path $repo "THIRD-PARTY-NOTICES.md") $publishDir
New-Item -ItemType Directory -Path (Join-Path $publishDir "LICENSES") -Force | Out-Null
Copy-Item (Join-Path $repo "LICENSES/HidSharp-Apache-2.0.txt") (Join-Path $publishDir "LICENSES")
Compress-Archive -Path "$publishDir/*" -DestinationPath $zipPath -CompressionLevel Optimal
Write-Output "Created $zipPath"
