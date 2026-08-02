$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$corePath = Join-Path $root 'src/AllyBindings.Core/AsusRearButtonProtocol.cs'
$backendPath = Join-Path $root 'src/AllyBindings.Core/ControllerBackend.cs'
$extractorPath = Join-Path $root 'src/AllyBindings.Core/UsbEtwHidFeatureReportExtractor.cs'
$discoveryPath = Join-Path $root 'src/AllyBindings.Core/UsbEtwSchemaDiscovery.cs'
$phasePath = Join-Path $root 'src/AllyBindings.Core/UsbEtwCapturePhases.cs'
$boundedReaderPath = Join-Path $root 'src/AllyBindings.Core/BoundedTextLineReader.cs'
$resetGatePath = Join-Path $root 'src/AllyBindings.Core/CaptureResetGate.cs'
$discoveryContractPath = Join-Path $root 'src/AllyBindings.Core/UsbEtwSchemaDiscoveryContract.cs'
$servicePath = Join-Path $root 'src/AllyBindings.Windows/ArmouryCaptureService.cs'
$helperPath = Join-Path $root 'src/AllyBindings.Windows/ArmouryEtwCaptureHelper.cs'
$diagnosticsPath = Join-Path $root 'src/AllyBindings.Windows/ArmouryCaptureDiagnostics.cs'
$appPath = Join-Path $root 'src/AllyBindings.Windows/App.xaml.cs'
$xamlPath = Join-Path $root 'src/AllyBindings.Windows/MainWindow.xaml'
$projectPath = Join-Path $root 'src/AllyBindings.Windows/AllyBindings.Windows.csproj'
$buildWorkflowPath = Join-Path $root '.github/workflows/build.yml'
$releaseWorkflowPath = Join-Path $root '.github/workflows/release.yml'

$core = Get-Content -Raw -LiteralPath $corePath
$backend = Get-Content -Raw -LiteralPath $backendPath
$extractor = Get-Content -Raw -LiteralPath $extractorPath
$discovery = Get-Content -Raw -LiteralPath $discoveryPath
$phase = Get-Content -Raw $phasePath
$boundedReader = Get-Content -Raw $boundedReaderPath
$resetGate = Get-Content -Raw $resetGatePath
$discoveryContract = Get-Content -Raw -LiteralPath $discoveryContractPath
$service = Get-Content -Raw -LiteralPath $servicePath
$helper = Get-Content -Raw -LiteralPath $helperPath
$diagnostics = Get-Content -Raw -LiteralPath $diagnosticsPath
$app = Get-Content -Raw -LiteralPath $appPath
$xaml = Get-Content -Raw -LiteralPath $xamlPath
$project = Get-Content -Raw -LiteralPath $projectPath
$buildWorkflow = Get-Content -Raw -LiteralPath $buildWorkflowPath
$releaseWorkflow = Get-Content -Raw -LiteralPath $releaseWorkflowPath

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
    'EnableProviderTimeoutMSec',
    'MaximumCaptureDuration',
    'MaximumRetainedReports',
    'MaximumSchemaShapes',
    'MaximumSchemaShapesPerPhase',
    'MaximumMarkerShapes',
    'MaximumMarkerShapesPerPhase',
    'MaximumPayloadProperties',
    'MaximumDecodedPayloadProperties',
    'MaximumMetadataCharacters',
    'MaximumSchemaDiscoveryBytes',
    'MaximumObservedEvents',
    'MaximumDecodedBinaryBytes',
    'VerifyParentExecutableIdentity')) {
    if ($helper.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The integrated ETW helper is missing safety/lifecycle control: $required"
    }
}
if ($helper.IndexOf('eventsLost = session.EventsLost', [StringComparison]::Ordinal) -gt
    $helper.IndexOf('session.Stop()', [StringComparison]::Ordinal)) {
    throw 'ETW loss is queried after destroying the live trace session.'
}
foreach ($required in @('helper-started', 'helper-pipe-connected', 'helper-providers-verified', 'helper-session-created', 'helper-ready-sent', 'helper-failed')) {
    if ($helper.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The ETW helper diagnostic is missing lifecycle stage $required"
    }
}
foreach ($required in @('SchemaVersion', 'Breadcrumbs', 'Errors', 'MaximumDiagnosticBytes', 'RetentionAge', 'Sanitize', 'public static void Delete', 'File.Move(temporary, path, overwrite: true)')) {
    if ($diagnostics.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The bounded in-app diagnostic is missing requirement $required"
    }
}
if ($app.IndexOf('if (copyDiagnostics', [StringComparison]::Ordinal) -lt 0 -or
    $app.IndexOf('System.Windows.Clipboard.SetText(deferredDiagnosticText)', [StringComparison]::Ordinal) -lt 0 -or
    $app.IndexOf('Copy diagnostics', [StringComparison]::Ordinal) -lt 0 -or
    $app.IndexOf('Open folder', [StringComparison]::Ordinal) -lt 0 -or
    $app.IndexOf('deferredDiagnostic.DiagnosticPath', [StringComparison]::Ordinal) -lt 0) {
    throw 'Capture failures do not expose copy/open diagnostics actions in the controller-safe app flow.'
}
if ($service.IndexOf('parent-completion-failed', [StringComparison]::Ordinal) -lt 0 -or
    $service.IndexOf('CompleteCoreAsync', [StringComparison]::Ordinal) -lt 0 -or
    $service.IndexOf('ex is not OperationCanceledException and not ArmouryCaptureException', [StringComparison]::Ordinal) -lt 0) {
    throw 'Capture completion failures are not consistently wrapped with an in-app diagnostic.'
}
$startMethod = $service.Substring(
    $service.IndexOf('public async Task<ArmouryCaptureSession> StartAsync', [StringComparison]::Ordinal),
    $service.IndexOf('public async Task<ArmouryCaptureResult> CompleteAsync', [StringComparison]::Ordinal) -
    $service.IndexOf('public async Task<ArmouryCaptureSession> StartAsync', [StringComparison]::Ordinal))
if ($startMethod.IndexOf('try', [StringComparison]::Ordinal) -gt
    $startMethod.IndexOf('DiscoverTargetAsync(cancellationToken)', [StringComparison]::Ordinal) -or
    $startMethod.IndexOf('catch (OperationCanceledException)', [StringComparison]::Ordinal) -lt 0) {
    throw 'Pre-capture target rediscovery is outside the diagnostic boundary or cancellation is retained as a failure.'
}
$cancelMethod = $service.Substring(
    $service.IndexOf('public void CancelAndDelete()', [StringComparison]::Ordinal),
    $service.IndexOf('private static void TryDispose', [StringComparison]::Ordinal) -
    $service.IndexOf('public void CancelAndDelete()', [StringComparison]::Ordinal))
if ($cancelMethod.IndexOf('ArmouryCaptureDiagnostics.Delete(SessionId)', [StringComparison]::Ordinal) -lt 0) {
    throw 'A cancelled active capture retains its lifecycle diagnostic.'
}
if ($service -notmatch 'Stopwatch\.GetTimestamp\(\)' -or
    $service -notmatch 'PerformanceCounterTimestamp' -or
    $extractor -notmatch 'PerformanceCounterTimestamp') {
    throw 'Action markers and ETW reports are not correlated on the shared QPC clock.'
}
if (($service | Select-String -Pattern 'schemaVersion\s*=\s*5' -AllMatches).Matches.Count -ne 2) {
    throw 'Capture report and manifest are not both stamped with QPC-phase schema version 5.'
}
if ($extractor -notmatch '0x5A,\s*0xD1,\s*0x02,\s*0x08,\s*0x2C' -or
    $extractor -notmatch 'AsusRearButtonProtocol\.ReportLength' -or
    $extractor -notmatch 'MaximumWireReportLength') {
    throw 'The evidence extractor no longer requires the exact ASUS report prefix and wire length.'
}
if ($discovery -notmatch '0xD1,\s*0x02,\s*0x08,\s*0x2C' -or
    $discovery -notmatch 'payload bytes are never returned' -or
    $discovery -match 'record UsbEtwMarkerObservation\([^)]*byte\[\]') {
    throw 'Schema discovery is missing the metadata-only command-marker contract.'
}
if ($service -notmatch 'Environment\.ProcessPath' -or
    $service -notmatch 'Verb\s*=\s*"runas"' -or
    $service -notmatch 'ArmouryEtwCaptureHelper\.HelperArgument') {
    throw 'Capture does not self-elevate the same Ally Bindings executable as its ETW helper.'
}
if ($service -notmatch 'ArmouryEtwCapturePipe\.CreateServer\(sessionId\)' -or
    $service -notmatch 'GetNamedPipeClientProcessId' -or
    $service -notmatch 'clientProcessId\s*!=\s*\(uint\)helper\.Id' -or
    $helper -notmatch 'NamedPipeClientStream' -or
    $helper -notmatch 'NamedPipeServerStreamAcl\.Create' -or
    $helper -notmatch 'WellKnownSidType\.NetworkSid' -or
    $helper -notmatch 'AccessControlType\.Deny' -or
    $helper -notmatch 'AccessControlType\.Allow' -or
    $helper -notmatch 'SetOwner\(userSid\)' -or
    $helper -notmatch 'GetNamedPipeServerProcessId' -or
    $helper -notmatch 'serverProcessId\s*!=\s*\(uint\)parentProcessId') {
    throw 'The cross-elevation ETW pipe is not current-user/local-only with mutual PID authentication.'
}
foreach ($workflow in @($buildWorkflow, $releaseWorkflow)) {
    if ($workflow.IndexOf('test-etw-helper-auth.ps1', [StringComparison]::Ordinal) -lt 0) {
        throw 'A shipping workflow does not run the behavioral ETW helper authentication test.'
    }
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
if ($service -notmatch 'AssemblyInformationalVersionAttribute' -or
    $service -match 'applicationVersion\s*=\s*typeof\(ArmouryCaptureService\)\.Assembly\.GetName\(\)\.Version') {
    throw 'Capture evidence is not stamped with the exact informational build version.'
}
if ($service -match 'WriteArtifactAsync') {
    throw 'Successful capture still retains loose duplicate private artifacts outside the ZIP.'
}
if ($service -notmatch '\.tmp-' -or
    $service -notmatch 'File\.Move\(temporaryPath, bundlePath\)' -or
    $app -notmatch 'PRIVACY WARNING') {
    throw 'Capture artifacts are not atomically committed or cleanup failures can remain silent.'
}
if ($service -notmatch 'captureScopeVerified:\s*false' -or
    $service -notmatch 'hardwareUnlockEvidence\s*=\s*false' -or
    $service -notmatch 'Discovery metadata is never hardware-unlock evidence' -or
    $app -match 'staleRecoveryMarker\s*&&\s*result\.IsConclusive') {
    throw 'Unvalidated ETW candidates can still become conclusive unlock/recovery evidence.'
}
if ($service -match 'diagnosticCandidates|retainedHex|RetainedBytes' -or
    $helper -match 'UsbEtwDiagnosticCandidate|RetainedBytes' -or
    $service -notmatch 'containsPayloadBytes\s*[:=]\s*false' -or
    $service -notmatch 'UsbEtwCapturePhaseCommand\.Format\(transition\.Phase, transition\.Kind\)' -or
    $service -notmatch 'acknowledgement\.Type\.Equals\("phase-ack"' -or
    $helper -notmatch 'eventQpc\s*=\s*data\.TimeStampQPC' -or
    $helper -notmatch 'capturePhases\.Classify\(eventQpc\)' -or
    $helper -notmatch 'UsbEtwCapturePhaseCommand\.TryParse' -or
    $helper -notmatch 'BoundaryQpc:\s*boundaryQpc' -or
    $phase -notmatch 'lock \(_gate\)' -or
    $phase -notmatch 'Stopwatch\.GetTimestamp\(\)' -or
    $phase -notmatch 'StartNow' -or $phase -notmatch 'EndNow' -or
    $discoveryContract -notmatch 'record UsbEtwSchemaDiscoveryReport') {
    throw 'Schema discovery is not metadata-only and QPC-phase-bucketed.'
}
foreach ($phaseMarker in @(
    'step-started-m1-a-m2-b',
    'armoury-applied-m1-a-m2-b',
    'step-started-m1-x-m2-y',
    'armoury-applied-m1-x-m2-y',
    'step-started-reset-to-default',
    'armoury-reset-m1-m2-to-default')) {
    if ($service -notmatch [regex]::Escape($phaseMarker) -or
        $app -notmatch [regex]::Escape($phaseMarker)) {
        throw "The capture phase protocol is not wired to the UI action marker '$phaseMarker'."
    }
}
$startAckPattern = 'await\s+captureService\.MarkActionAsync\(session,\s*"step-started-[^"]+"[^;]*;\s*await\s+RequireCaptureStepAsync'
if ([regex]::Matches($app, $startAckPattern).Count -ne 3) {
    throw 'A capture action prompt can appear before the helper acknowledges its QPC start boundary.'
}
$restoreStart = $app.IndexOf('public async Task RestoreDefaultAsync', [StringComparison]::Ordinal)
$restoreEnd = $app.IndexOf('public string BuildDiagnostics()', [StringComparison]::Ordinal)
$restoreMethod = $app.Substring($restoreStart, $restoreEnd - $restoreStart)
if ($restoreMethod.IndexOf('CaptureResetGate.AcquireWhenCaptureStoppedAsync', [StringComparison]::Ordinal) -lt 0 -or
    $resetGate -notmatch 'await operationGate\.WaitAsync' -or
    $resetGate -notmatch 'captureCompletion = getActiveCaptureCompletion\(\)' -or
    $resetGate -notmatch 'requestCaptureCancellation\(\)' -or
    $resetGate -notmatch 'await captureCompletion\.WaitAsync') {
    throw 'Native reset can still begin before active ETW capture cancellation has completed.'
}
if ($service -notmatch 'helperExitVerified = HelperProcess\.HasExited \|\| HelperProcess\.WaitForExit\(5_000\)' -or
    $service -notmatch '!HelperProcess\.WaitForExit\(5_000\) \|\| !HelperProcess\.HasExited' -or
    $service -notmatch 'Native controller resets remain blocked' -or
    $app -notmatch 'captureCompletion\?\.TrySetException' -or
    $app -notmatch 'if \(captureTeardownFailure is null\)' -or
    $app -notmatch 'if \(_armouryCaptureInProgress \|\| _armouryCaptureTeardownUnconfirmed\)') {
    throw 'Capture completion can still authorize a native reset without positively verified ETW helper exit.'
}
if ($service -notmatch 'helperExitVerified = helper\.HasExited \|\| helper\.WaitForExit\(5_000\)' -or
    $service -notmatch 'ArmouryCaptureTeardownException' -or
    $app -notmatch 'private async Task<bool> ConfirmSafeExitForUpdateAsync\(\)[\s\S]*CaptureResetGate\.AcquireWhenCaptureStoppedAsync') {
    throw 'Capture startup or updater shutdown can still bypass verified helper teardown.'
}
if ($app -notmatch 'private async Task ExitAsync\(\)[\s\S]*CaptureResetGate\.AcquireWhenCaptureStoppedAsync' -or
    $app -notmatch 'if \(_exiting \|\| _armouryCaptureInProgress\) return;' -or
    $app -notmatch 'OnSessionEnding\(SessionEndingCancelEventArgs e\)[\s\S]*_exiting = true;') {
    throw 'Exit or session-ending can still race queued capture startup.'
}

if ($service -notmatch 'BoundedTextLineReader' -or $helper -notmatch 'BoundedTextLineReader' -or
    $boundedReader -notmatch 'Array\.IndexOf' -or $boundedReader -notmatch '_offset') {
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
$captureBarrierIndex = $captureMethod.IndexOf('captureCompletion?.TrySet', [StringComparison]::Ordinal)
$deferredFailureUiIndex = $captureMethod.IndexOf('if (deferredFailureMessage is not null', [StringComparison]::Ordinal)
if ($captureBarrierIndex -lt 0 -or $deferredFailureUiIndex -lt 0 -or $captureBarrierIndex -gt $deferredFailureUiIndex) {
    throw 'Capture completion is still blocked behind failure-dialog interaction.'
}
$discoverTarget = $captureMethod.IndexOf('DiscoverTargetAsync(cancellationToken)', [StringComparison]::Ordinal)
$confirmTarget = $captureMethod.IndexOf('No ETW session has started yet.', [StringComparison]::Ordinal)
$startCapture = $captureMethod.IndexOf('StartAsync(target, cancellationToken)', [StringComparison]::Ordinal)
if ($discoverTarget -lt 0 -or $confirmTarget -le $discoverTarget -or $startCapture -le $confirmTarget) {
    throw 'The ETW session starts before explicit confirmation of the discovered ASUS HID target.'
}
if ($captureMethod.IndexOf('CompleteAsync(session, cancellationToken)', [StringComparison]::Ordinal) -lt 0) {
    throw 'Armoury capture completion does not observe panic/exit cancellation.'
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
