param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-MethodBody([string]$Source, [string]$Start, [string]$End) {
    $startIndex = $Source.IndexOf($Start, [StringComparison]::Ordinal)
    Assert-True ($startIndex -ge 0) "Missing method marker: $Start"
    $endIndex = $Source.IndexOf($End, $startIndex + $Start.Length, [StringComparison]::Ordinal)
    Assert-True ($endIndex -gt $startIndex) "Missing method boundary: $End"
    return $Source.Substring($startIndex, $endIndex - $startIndex)
}

$coreProtocol = Get-Content -Raw (Join-Path $Root 'src/AllyBindings.Core/AsusRearButtonProtocol.cs')
$device = Get-Content -Raw (Join-Path $Root 'src/AllyBindings.Windows/AsusRearButtonHidDevice.cs')
$analyzer = Get-Content -Raw (Join-Path $Root 'src/AllyBindings.Core/AsusFeatureReportSnapshot.cs')
$service = Get-Content -Raw (Join-Path $Root 'src/AllyBindings.Windows/AsusFeatureReportSnapshotService.cs')
$app = Get-Content -Raw (Join-Path $Root 'src/AllyBindings.Windows/App.xaml.cs')
$xaml = Get-Content -Raw (Join-Path $Root 'src/AllyBindings.Windows/MainWindow.xaml')
$retention = Get-Content -Raw (Join-Path $Root 'src/AllyBindings.Core/UsbEtwSchemaRetentionPolicy.cs')

Assert-True ($coreProtocol.Contains('Task<AsusRearButtonReadResult> ReadFeatureReportAsync(')) 'The device seam must expose a distinct read result.'
Assert-True ($coreProtocol.Contains('IReadOnlyList<AsusFeatureReportRead> Reads')) 'Read results must retain per-interface outcomes.'

$readMethod = Get-MethodBody $device 'public async Task<AsusRearButtonReadResult> ReadFeatureReportAsync(' 'public async Task<AsusRearButtonWriteResult> WriteFeatureReportAsync('
Assert-True (-not $readMethod.Contains('SetFeature', [StringComparison]::OrdinalIgnoreCase)) 'The public read seam must not reference SetFeature.'
Assert-True (-not $readMethod.Contains('WriteFeatureReport', [StringComparison]::OrdinalIgnoreCase)) 'The public read seam must not call the write path.'
Assert-True ($device.Contains('stream.GetFeature(buffer);')) 'The HID implementation must issue GetFeature.'
Assert-True (($device.Split('stream.GetFeature(buffer);').Length - 1) -eq 1) 'The HID implementation must have one bounded GetFeature call site.'
Assert-True ($device.Contains('reportLength is < AsusRearButtonProtocol.ReportLength or > UsbEtwHidFeatureReportExtractor.MaximumWireReportLength')) 'Reads must reject lengths outside 50-64 bytes.'
Assert-True ($device.Contains('no retry was attempted')) 'Read failures and timeouts must not retry.'

foreach ($forbidden in @('ArmouryEtw', 'NamedPipe', 'runas', 'WriteFeatureReportAsync(', 'SetFeature(')) {
    Assert-True (-not $service.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) "Snapshot service must not reference $forbidden"
}
Assert-True ($service.Contains('source = "read-only HidSharp GetFeature(0x5A) snapshot"')) 'Snapshot manifest must identify the read-only source.'
Assert-True ($service.Contains('rawSystemTraceWritten = false')) 'Snapshot manifest must deny raw trace output.'
Assert-True ($service.Contains('hardwareWriteAttempted = false')) 'Snapshot manifest must deny hardware writes.'
Assert-True ($service.Contains('hardwareUnlockEvidence = false')) 'Snapshot manifest must deny unlock authority.'
Assert-True ($service.Contains('userWritableBundleIsImmutableProvenance = false')) 'Snapshot manifest must state its provenance limitation.'
Assert-True ($service.Contains('minimumIndependentMatchingRuns = 2')) 'Snapshot bundle must require repeated physical evidence.'

Assert-True ($analyzer.Contains('HardwareUnlockEvidence: false')) 'Analyzer output must always be zero-authority.'
Assert-True ($analyzer.Contains('Readback analysis is review-required diagnostic evidence')) 'Analyzer must state review requirement.'
foreach ($forbidden in @('WriteFeatureReportAsync', 'SetFeature', 'CustomWritesApproved = true', 'RecoveryWritesApproved = true')) {
    Assert-True (-not $analyzer.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) "Analyzer must not contain $forbidden"
}

$appSnapshot = Get-MethodBody $app 'public async Task CaptureRearButtonSnapshotAsync()' 'public async Task CaptureArmouryProtocolAsync()'
foreach ($forbidden in @('ArmouryCaptureService', 'ArmouryEtw', 'MarkActionAsync', 'WriteFeatureReportAsync', 'SetFeature', 'runas')) {
    Assert-True (-not $appSnapshot.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) "Snapshot workflow must not reference $forbidden"
}
Assert-True ($appSnapshot.Contains('AsusFeatureReportSnapshotStage.Baseline')) 'Workflow must read the baseline stage.'
Assert-True ($appSnapshot.Contains('AsusFeatureReportSnapshotStage.M1A_M2B')) 'Workflow must read the A/B stage.'
Assert-True ($appSnapshot.Contains('AsusFeatureReportSnapshotStage.M1X_M2Y')) 'Workflow must read the X/Y stage.'
Assert-True ($appSnapshot.Contains('AsusFeatureReportSnapshotStage.ResetToDefault')) 'Workflow must read the reset stage.'
Assert-True ($xaml.Contains('AutomationProperties.AutomationId="RearButtonSnapshotButton"')) 'Snapshot action needs a stable automation ID.'
Assert-True ($xaml.Contains('No elevation, ETW, system-wide trace, pipe, driver, or HID write.')) 'UI must disclose the isolated read-only architecture.'

foreach ($token in @('"Mdl"', '"ReservedHcd"', '"TransferBuffer"')) {
    Assert-True ($retention.Contains($token)) "ETW nested deny policy must include $token"
}

Write-Host 'Feature snapshot safety contract passed.'
