param(
    [string]$ExecutablePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/AllyBindings-HardwareValidator-win-x64/AllyBindings.HardwareValidator.exe')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw "Hardware validator executable is missing: $ExecutablePath"
}

$auditRoot = Join-Path $env:ProgramData 'AllyBindings/HardwareValidator'
$before = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if (Test-Path -LiteralPath $auditRoot) {
    foreach ($file in Get-ChildItem -LiteralPath $auditRoot -File) {
        [void]$before.Add($file.FullName)
    }
}

$output = @(& $ExecutablePath inspect 2>&1 | ForEach-Object { $_.ToString() })
$exitCode = $LASTEXITCODE
if ($exitCode -notin @(0, 3)) {
    throw "Read-free inspect exited $exitCode instead of success or target-rejected: $($output -join [Environment]::NewLine)"
}
$joined = $output -join [Environment]::NewLine
if ($joined.IndexOf('DANGER: this performs one experimental hardware write', [StringComparison]::Ordinal) -ge 0 -or
    $joined.IndexOf('Type exactly:', [StringComparison]::Ordinal) -ge 0) {
    throw 'Inspect entered the hardware-write flow.'
}
if ($exitCode -eq 0 -and $joined.IndexOf('Inspection only. No HID feature report was read or written.', [StringComparison]::Ordinal) -lt 0) {
    throw 'Successful inspect did not make its no-read/no-write result explicit.'
}
if ($exitCode -eq 3 -and $joined.IndexOf('No hardware write was attempted.', [StringComparison]::Ordinal) -lt 0) {
    throw 'Rejected inspect did not make its no-write result explicit.'
}

$after = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if (Test-Path -LiteralPath $auditRoot) {
    foreach ($file in Get-ChildItem -LiteralPath $auditRoot -File) {
        [void]$after.Add($file.FullName)
    }
}
if (-not $before.SetEquals($after)) {
    throw 'Read-free inspect unexpectedly created or removed a hardware audit file.'
}

Write-Output "Hardware validator inspect smoke passed with exit code $exitCode and no audit mutation."
