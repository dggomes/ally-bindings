[CmdletBinding()]
param(
    [string]$ProjectPath = 'src/AllyBindings.Windows/AllyBindings.Windows.csproj',
    [string]$ExecutablePath = 'artifacts/AllyBindings-win-x64/AllyBindings.exe'
)

$ErrorActionPreference = 'Stop'
$evaluated = dotnet msbuild $ProjectPath -getProperty:Version -getProperty:FileVersion -getProperty:AssemblyVersion | ConvertFrom-Json
$expectedProduct = [string]$evaluated.Properties.Version
$expectedFile = [string]$evaluated.Properties.FileVersion
$expectedAssembly = [string]$evaluated.Properties.AssemblyVersion

if ($expectedProduct -notmatch '^\d+\.\d+\.\d+-[0-9A-Za-z.-]+$') {
    throw "Evaluated product version '$expectedProduct' is not a preview semantic version."
}
if ($expectedFile -cne $expectedAssembly) {
    throw "Evaluated FileVersion '$expectedFile' and AssemblyVersion '$expectedAssembly' differ."
}

$resolvedExecutable = Resolve-Path $ExecutablePath
$actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedExecutable)
if ($actual.FileVersion -cne $expectedFile) {
    throw "Built FileVersion '$($actual.FileVersion)' does not match '$expectedFile'."
}
$productPattern = '^' + [Regex]::Escape($expectedProduct) +
    '(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
if ($actual.ProductVersion -cnotmatch $productPattern) {
    throw "Built ProductVersion '$($actual.ProductVersion)' is neither exactly '$expectedProduct' nor that version with valid build metadata."
}

Write-Output "Windows version metadata validated: ProductVersion=$($actual.ProductVersion); FileVersion=$($actual.FileVersion)."
