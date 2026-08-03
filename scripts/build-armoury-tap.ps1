param(
    [string]$BuildDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'native/ArmouryTap/build')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$sourceDirectory = Join-Path $repo 'native/ArmouryTap'

$cmakeHelp = cmake --help | Out-String
if ($LASTEXITCODE -ne 0) { throw "cmake --help failed with exit code $LASTEXITCODE." }

$generators = @('Visual Studio 18 2026', 'Visual Studio 17 2022') |
    Where-Object { $cmakeHelp.IndexOf($_, [StringComparison]::Ordinal) -ge 0 } |
    Select-Object
if ($generators.Count -eq 0) {
    throw 'No supported x64 Visual Studio CMake generator is available (expected Visual Studio 2026 or 2022).'
}

$configuredGenerator = $null
foreach ($generator in $generators) {
    if (Test-Path -LiteralPath $BuildDirectory) {
        Remove-Item -LiteralPath $BuildDirectory -Recurse -Force
    }
    Write-Output "Configuring Armoury tap with CMake generator: $generator"
    cmake -S $sourceDirectory -B $BuildDirectory -G $generator -A x64
    if ($LASTEXITCODE -eq 0) {
        $configuredGenerator = $generator
        break
    }
    Write-Warning "CMake generator '$generator' could not configure the native build; trying the next supported generator."
}
if ([string]::IsNullOrWhiteSpace($configuredGenerator)) {
    throw 'No installed Visual Studio CMake generator could configure the native x64 build.'
}
Write-Output "Building Armoury tap with CMake generator: $configuredGenerator"
cmake --build $BuildDirectory --config Release
if ($LASTEXITCODE -ne 0) { throw "cmake build failed with exit code $LASTEXITCODE." }

$nativeDll = Join-Path $BuildDirectory 'Release/AllyBindings.ArmouryTap.dll'
if (-not (Test-Path -LiteralPath $nativeDll -PathType Leaf)) {
    throw "Native Armoury tap output not found: $nativeDll"
}
Write-Output "Built native Armoury tap: $nativeDll"
