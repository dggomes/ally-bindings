$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$corePath = Join-Path $root 'src/AllyBindings.Core/AsusRearButtonProtocol.cs'
$backendPath = Join-Path $root 'src/AllyBindings.Core/ControllerBackend.cs'
$extractorPath = Join-Path $root 'src/AllyBindings.Core/UsbEtwHidFeatureReportExtractor.cs'
$servicePath = Join-Path $root 'src/AllyBindings.Windows/ArmouryCaptureService.cs'
$helperPath = Join-Path $root 'src/AllyBindings.Windows/ArmouryEtwCaptureHelper.cs'
$appPath = Join-Path $root 'src/AllyBindings.Windows/App.xaml.cs'
$xamlPath = Join-Path $root 'src/AllyBindings.Windows/MainWindow.xaml'
$projectPath = Join-Path $root 'src/AllyBindings.Windows/AllyBindings.Windows.csproj'

$core = Get-Content -Raw -LiteralPath $corePath
$backend = Get-Content -Raw -LiteralPath $backendPath
$extractor = Get-Content -Raw -LiteralPath $extractorPath
$service = Get-Content -Raw -LiteralPath $servicePath
$helper = Get-Content -Raw -LiteralPath $helperPath
$app = Get-Content -Raw -LiteralPath $appPath
$xaml = Get-Content -Raw -LiteralPath $xamlPath
$project = Get-Content -Raw -LiteralPath $projectPath

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

if ($project -notmatch 'Microsoft\.Diagnostics\.Tracing\.TraceEvent') {
    throw 'The Windows app is not bound to the in-process ETW consumer.'
}
foreach ($provider in @('Microsoft-Windows-USB-UCX', 'Microsoft-Windows-USB-USBXHCI', 'Microsoft-Windows-USB-USBHUB3')) {
    if ($helper.IndexOf($provider, [StringComparison]::Ordinal) -lt 0) {
        throw "The integrated logger does not probe built-in provider $provider"
    }
}
if ($helper -match 'if\s*\(\s*session\.EnableProvider') {
    throw 'TraceEventSession.EnableProvider return value is a session-restart flag, not a success flag.'
}
foreach ($required in @(
    'new TraceEventSession(CaptureSessionName)',
    'session.Source.Dynamic.All',
    'TraceEventProviders.GetProviderGuidByName',
    'UsbEtwHidFeatureReportExtractor.Extract',
    'data.PayloadValue(index)',
    'StopOnDispose = true',
    'session.Stop()',
    'MaximumEventPayloadBytes',
    'FullDataTraceKeywords = 0x8101',
    'session.EventsLost',
    'MaximumCaptureDuration',
    'MaximumRetainedReports')) {
    if ($helper.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The integrated ETW helper is missing safety/lifecycle control: $required"
    }
}
if ($extractor -notmatch '0x5A,\s*0xD1,\s*0x02,\s*0x08,\s*0x2C' -or
    $extractor -notmatch 'AsusRearButtonProtocol\.ReportLength' -or
    $extractor -notmatch 'MaximumWireReportLength') {
    throw 'The ETW extractor no longer requires the exact ASUS report prefix and report length.'
}
if ($service -notmatch 'Environment\.ProcessPath' -or
    $service -notmatch 'Verb\s*=\s*"runas"' -or
    $service -notmatch 'ArmouryEtwCaptureHelper\.HelperArgument') {
    throw 'Capture does not self-elevate the same Ally Bindings executable as its ETW helper.'
}
if ($service -notmatch 'PipeOptions\.Asynchronous\s*\|\s*PipeOptions\.CurrentUserOnly' -or
    $service -notmatch 'GetNamedPipeClientProcessId' -or
    $service -notmatch 'clientProcessId\s*!=\s*\(uint\)helper\.Id' -or
    $helper -notmatch 'NamedPipeClientStream' -or
    $helper -notmatch 'PipeOptions\.Asynchronous\s*\|\s*PipeOptions\.CurrentUserOnly' -or
    $helper -notmatch 'GetNamedPipeServerProcessId' -or
    $helper -notmatch 'serverProcessId\s*!=\s*\(uint\)parentProcessId') {
    throw 'The parent and elevated helper do not mutually authenticate over a current-user-only named pipe.'
}
if ($app.IndexOf('TryParseArguments(e.Args', [StringComparison]::Ordinal) -gt
    $app.IndexOf('new Mutex(', [StringComparison]::Ordinal)) {
    throw 'The ETW helper is still subject to the normal single-instance mutex.'
}
if ($app -notmatch '_executableIntegrityLock' -or
    $app -notmatch 'System\.IO\.FileShare\.Read' -or
    $app -match 'System\.IO\.FileShare\.(?:Write|Delete)') {
    throw 'The running executable is not held read-only across self-elevation.'
}
if ($service -notmatch 'rawSystemTraceWritten\s*=\s*false' -or
    $service -notmatch 'No USBPcap/Wireshark driver, raw ETL, or raw PCAP was written') {
    throw 'The bundle no longer records the no-raw-trace privacy invariant.'
}
if ($service -notmatch 'captureScopeVerified:\s*false' -or
    $service -notmatch 'hardwareUnlockEvidence\s*=\s*false' -or
    $app -match 'staleRecoveryMarker\s*&&\s*result\.IsConclusive') {
    throw 'Unvalidated ETW candidates can still become conclusive unlock/recovery evidence.'
}
if ($service -notmatch 'ReadBoundedLineAsync' -or $helper -notmatch 'ReadBoundedLineAsync') {
    throw 'The ETW IPC protocol does not bound both command and response messages.'
}
foreach ($forbidden in @('TraceEventSession(sessionName,', 'data.EventData()', 'USBPcapCMD', 'logman.exe', 'tracerpt.exe', 'File.WriteAllBytes', 'SetFeature', 'WriteFeatureReport', 'IControllerBackend')) {
    foreach ($source in @($service, $helper, $extractor)) {
        if ($source.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "The integrated capture path contains forbidden raw-capture/write token: $forbidden"
        }
    }
}

$captureStart = $app.IndexOf('public async Task CaptureArmouryProtocolAsync()', [StringComparison]::Ordinal)
$captureEnd = $app.IndexOf('public async Task CheckForUpdatesAsync', $captureStart, [StringComparison]::Ordinal)
if ($captureStart -lt 0 -or $captureEnd -le $captureStart) {
    throw 'Could not isolate CaptureArmouryProtocolAsync for the no-write assertion.'
}
$captureMethod = $app.Substring($captureStart, $captureEnd - $captureStart)
$discoverTarget = $captureMethod.IndexOf('DiscoverTargetAsync()', [StringComparison]::Ordinal)
$confirmTarget = $captureMethod.IndexOf('No ETW session has started yet.', [StringComparison]::Ordinal)
$startCapture = $captureMethod.IndexOf('StartAsync(target)', [StringComparison]::Ordinal)
if ($discoverTarget -lt 0 -or $confirmTarget -le $discoverTarget -or $startCapture -le $confirmTarget) {
    throw 'The ETW session starts before explicit confirmation of the discovered ASUS HID target.'
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

Write-Output 'Integrated ETW capture safety and privacy assertions passed.'
