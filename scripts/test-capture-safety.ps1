$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$corePath = Join-Path $root 'src/AllyBindings.Core/AsusRearButtonProtocol.cs'
$backendPath = Join-Path $root 'src/AllyBindings.Core/ControllerBackend.cs'
$servicePath = Join-Path $root 'src/AllyBindings.Windows/ArmouryCaptureService.cs'
$appPath = Join-Path $root 'src/AllyBindings.Windows/App.xaml.cs'
$xamlPath = Join-Path $root 'src/AllyBindings.Windows/MainWindow.xaml'

$core = Get-Content -Raw -LiteralPath $corePath
$backend = Get-Content -Raw -LiteralPath $backendPath
$service = Get-Content -Raw -LiteralPath $servicePath
$app = Get-Content -Raw -LiteralPath $appPath
$xaml = Get-Content -Raw -LiteralPath $xamlPath

if ($core -notmatch 'CustomWritesApproved\s*=>\s*false') {
    throw 'Custom M1/M2 writes are not source-locked.'
}
if ($core -notmatch 'RecoveryWritesApproved\s*=>\s*false') {
    throw 'Recovery M1/M2 writes are not source-locked.'
}
if ($core -notmatch 'isRecoveryReset\s*\?\s*recoveryWritesApproved\s*:\s*customWritesApproved\s*&&\s*recoveryWritesApproved') {
    throw 'Custom and recovery operations do not use independent authorization gates.'
}
if ($backend -match 'status\.CanRemap\s*\|\|') {
    throw 'A recovery write can still inherit custom-write authorization through BackendStatus.CanRemap.'
}
foreach ($forbidden in @('customWritesApproved =', 'allowUnverifiedRecoveryReset =')) {
    if ($backend.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "The public backend exposes a caller-controlled write gate: $forbidden"
    }
}
if ($service -notmatch '--devices \{target\.Address\}') {
    throw 'The passive logger no longer supplies a USBPcap device-address filter.'
}
if ($service -notmatch 'start \\"\\" /wait') {
    throw 'USBPcapCMD is not launched with a tracked start /wait wrapper.'
}
if ($service -notmatch 'JobObjectLimitKillOnJobClose' -or $service -notmatch 'ally-bindings-owns-capture\.signal') {
    throw 'The USBPcap process is not bound to a kill-on-close job before capture starts.'
}
if ($service -notmatch 'processJob\s+is\s+null[\s\S]*?Kill\(entireProcessTree:\s*true\)') {
    throw 'USBPcap has no explicit process-tree kill fallback when Job Object assignment fails.'
}
foreach ($unsafePathToken in @("'^'", "'!'", "'%'")) {
    if ($service.IndexOf($unsafePathToken, [StringComparison]::Ordinal) -lt 0) {
        throw "The generated CMD path validation does not reject metacharacter $unsafePathToken"
    }
}
foreach ($forbidden in @('--capture-from-all-devices', 'SetFeature', 'WriteFeatureReport', 'AsusRearButtonHidDevice', 'IControllerBackend')) {
    if ($service.IndexOf($forbidden, [StringComparison]::Ordinal) -ge 0) {
        throw "The passive logger contains forbidden write/broad-capture token: $forbidden"
    }
}

$captureStart = $app.IndexOf('public async Task CaptureArmouryProtocolAsync()', [StringComparison]::Ordinal)
$captureEnd = $app.IndexOf('public async Task CheckForUpdatesAsync', $captureStart, [StringComparison]::Ordinal)
if ($captureStart -lt 0 -or $captureEnd -le $captureStart) {
    throw 'Could not isolate CaptureArmouryProtocolAsync for the no-write assertion.'
}
$captureMethod = $app.Substring($captureStart, $captureEnd - $captureStart)
$discoverTarget = $captureMethod.IndexOf('DiscoverTargetAsync()', [StringComparison]::Ordinal)
$confirmTarget = $captureMethod.IndexOf('No capture has started yet.', [StringComparison]::Ordinal)
$startCapture = $captureMethod.IndexOf('StartAsync(target)', [StringComparison]::Ordinal)
if ($discoverTarget -lt 0 -or $confirmTarget -le $discoverTarget -or $startCapture -le $confirmTarget) {
    throw 'The capture starts before explicit confirmation of the discovered ASUS USB target.'
}
foreach ($forbidden in @('_backend.', 'ApplyAsync(', 'RestoreDefaultAsync(', 'WriteFeatureReport')) {
    if ($captureMethod.IndexOf($forbidden, [StringComparison]::Ordinal) -ge 0) {
        throw "CaptureArmouryProtocolAsync contains a forbidden backend/write call: $forbidden"
    }
}
if ($app -notmatch 'enableRearButtons\s*&&\s*ArmouryProtocolValidation\.IsOperationApproved\(isRecoveryReset:\s*false\)' -or
    $app -notmatch 'allowUnverifiedRecoveryReset\s*&&\s*ArmouryProtocolValidation\.RecoveryWritesApproved') {
    throw 'Windows backend construction is not guarded by operation-specific validation gates.'
}
if ($xaml -notmatch 'IsEnabled="\{Binding CanEnableAsusRearButtonMappings\}"') {
    throw 'The ASUS write opt-in UI is not visibly locked.'
}

Write-Output 'Passive capture safety assertions passed.'
