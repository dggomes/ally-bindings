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

# Build the native Armoury tap DLL so it can be embedded as a resource.
$cmakeBuild = Join-Path $repo "native/ArmouryTap/build"
& (Join-Path $repo 'scripts/build-armoury-tap.ps1') -BuildDirectory $cmakeBuild
Assert-NativeSuccess "native Armoury tap build"

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

# Public preview packages do not ship portable symbols. They add no runtime
# value, expose source paths, and violate the lean single-app package contract.
Get-ChildItem -LiteralPath $publishDir -Filter '*.pdb' -File | Remove-Item -Force

Copy-Item (Join-Path $repo "README.md") $publishDir
Copy-Item (Join-Path $repo "CHANGELOG.md") $publishDir
Copy-Item (Join-Path $repo "SECURITY.md") $publishDir
Copy-Item (Join-Path $repo "LICENSE") $publishDir
Copy-Item (Join-Path $repo "THIRD-PARTY-NOTICES.md") $publishDir
Copy-Item (Join-Path $repo "CONTRIBUTING.md") $publishDir
Copy-Item (Join-Path $repo "docs") (Join-Path $publishDir "docs") -Recurse
Copy-Item (Join-Path $repo "docs/ARMOURY-TAP-SECURITY.md") (Join-Path $publishDir "docs/ARMOURY-TAP-SECURITY.md") -Force
Copy-Item (Join-Path $repo "docs/ARMOURY-TAP-USER-GUIDE.md") (Join-Path $publishDir "docs/ARMOURY-TAP-USER-GUIDE.md") -Force
New-Item -ItemType Directory -Path (Join-Path $publishDir "LICENSES") -Force | Out-Null
Copy-Item (Join-Path $repo "LICENSES/*") (Join-Path $publishDir "LICENSES")
Compress-Archive -Path "$publishDir/*" -DestinationPath $zipPath -CompressionLevel Optimal
& (Join-Path $repo 'scripts/test-release-package.ps1') -PackageRoot $publishDir -ZipPath $zipPath
Write-Output "Created $zipPath"
