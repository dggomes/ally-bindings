$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$corePath = Join-Path $root 'src/AllyBindings.Core/AsusRearButtonProtocol.cs'
$backendPath = Join-Path $root 'src/AllyBindings.Core/ControllerBackend.cs'
$extractorPath = Join-Path $root 'src/AllyBindings.Core/UsbEtwHidFeatureReportExtractor.cs'
$discoveryPath = Join-Path $root 'src/AllyBindings.Core/UsbEtwSchemaDiscovery.cs'
$payloadFlattenerPath = Join-Path $root 'src/AllyBindings.Core/UsbEtwPayloadFlattener.cs'
$schemaRetentionPolicyPath = Join-Path $root 'src/AllyBindings.Core/UsbEtwSchemaRetentionPolicy.cs'
$phasePath = Join-Path $root 'src/AllyBindings.Core/UsbEtwCapturePhases.cs'
$boundedReaderPath = Join-Path $root 'src/AllyBindings.Core/BoundedTextLineReader.cs'
$resetGatePath = Join-Path $root 'src/AllyBindings.Core/CaptureResetGate.cs'
$candidateAttachmentCoordinatorPath = Join-Path $root 'src/AllyBindings.Core/CandidateAttachmentCoordinator.cs'
$profileStorePath = Join-Path $root 'src/AllyBindings.Core/JsonProfileStore.cs'
$discoveryContractPath = Join-Path $root 'src/AllyBindings.Core/UsbEtwSchemaDiscoveryContract.cs'
$servicePath = Join-Path $root 'src/AllyBindings.Windows/ArmouryCaptureService.cs'
$helperPath = Join-Path $root 'src/AllyBindings.Windows/ArmouryEtwCaptureHelper.cs'
$diagnosticsPath = Join-Path $root 'src/AllyBindings.Windows/ArmouryCaptureDiagnostics.cs'
$appPath = Join-Path $root 'src/AllyBindings.Windows/App.xaml.cs'
$xamlPath = Join-Path $root 'src/AllyBindings.Windows/MainWindow.xaml'
$projectPath = Join-Path $root 'src/AllyBindings.Windows/AllyBindings.Windows.csproj'
$buildWorkflowPath = Join-Path $root '.github/workflows/build.yml'
$releaseWorkflowPath = Join-Path $root '.github/workflows/release.yml'
$packageScriptPath = Join-Path $root 'scripts/package.ps1'
$nativeBuildScriptPath = Join-Path $root 'scripts/build-armoury-tap.ps1'
$embeddedTapTestPath = Join-Path $root 'scripts/test-armoury-tap-embedded-resource.ps1'

$core = Get-Content -Raw -LiteralPath $corePath
$backend = Get-Content -Raw -LiteralPath $backendPath
$extractor = Get-Content -Raw -LiteralPath $extractorPath
$discovery = Get-Content -Raw -LiteralPath $discoveryPath
$payloadFlattener = Get-Content -Raw -LiteralPath $payloadFlattenerPath
$schemaRetentionPolicy = Get-Content -Raw -LiteralPath $schemaRetentionPolicyPath
$phase = Get-Content -Raw $phasePath
$boundedReader = Get-Content -Raw $boundedReaderPath
$resetGate = Get-Content -Raw $resetGatePath
$candidateAttachmentCoordinator = Get-Content -Raw -LiteralPath $candidateAttachmentCoordinatorPath
$profileStore = Get-Content -Raw -LiteralPath $profileStorePath
$discoveryContract = Get-Content -Raw -LiteralPath $discoveryContractPath
$service = Get-Content -Raw -LiteralPath $servicePath
$helper = Get-Content -Raw -LiteralPath $helperPath
$diagnostics = Get-Content -Raw -LiteralPath $diagnosticsPath
$app = Get-Content -Raw -LiteralPath $appPath
$xaml = Get-Content -Raw -LiteralPath $xamlPath
$project = Get-Content -Raw -LiteralPath $projectPath
$buildWorkflow = Get-Content -Raw -LiteralPath $buildWorkflowPath
$releaseWorkflow = Get-Content -Raw -LiteralPath $releaseWorkflowPath
$packageScript = Get-Content -Raw -LiteralPath $packageScriptPath
$nativeBuildScript = Get-Content -Raw -LiteralPath $nativeBuildScriptPath
$embeddedTapTest = Get-Content -Raw -LiteralPath $embeddedTapTestPath

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
    'MaximumPrioritySchemaShapes',
    'MaximumPrioritySchemaShapesPerPhase',
    'MaximumFramingSchemaShapes',
    'MaximumFramingSchemaShapesPerPhase',
    'MaximumMarkerShapes',
    'MaximumMarkerShapesPerPhase',
    'MaximumPayloadProperties',
    'MaximumDecodedPayloadProperties',
    'MaximumPayloadNestingDepth',
    'MaximumVisitedPayloadNodes',
    'MaximumMetadataCharacters',
    'MaximumSchemaDiscoveryBytes',
    'MaximumObservedEvents',
    'MaximumDecodedBinaryBytes',
    'VerifyParentExecutableIdentity')) {
    if ($helper.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The integrated ETW helper is missing safety/lifecycle control: $required"
    }
}
if ($helper -notmatch 'UsbEtwPayloadFlattener\.Flatten' -or
    $helper -notmatch 'UsbEtwSchemaRetentionPolicy\.Classify' -or
    $helper -notmatch 'UsbEtwPrioritizedSchemaCounter' -or
    $helper -notmatch 'MaximumPrioritySchemaShapes' -or
    $helper -notmatch 'MaximumFramingSchemaShapes' -or
    $helper -notmatch 'UsbEtwSchemaDiscovery\.Inspect\(\s*fields\.DiscoveryFields' -or
    $payloadFlattener -notmatch 'IEnumerable<KeyValuePair<string, object>>' -or
    $payloadFlattener -notmatch 'ReferenceEqualityComparer\.Instance' -or
    $payloadFlattener -match 'JsonSerializer|WriteAll|FileStream|ToString\(' -or
    $schemaRetentionPolicy -notmatch 'Microsoft-Windows-USB-UCX' -or
    $schemaRetentionPolicy -notmatch 'URB_FUNCTION_' -or
    $schemaRetentionPolicy -notmatch 'fid_UCX_URB_' -or
    $schemaRetentionPolicy -notmatch 'fid_URB_TransferData' -or
    $schemaRetentionPolicy -match 'PayloadValue|MarkerComparableBytes|byte\[\]') {
    throw 'Nested ETW payload inspection is not bounded, transient and focused on metadata-only UCX URB framing.'
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
if ([regex]::Matches($service, 'schemaVersion\s*=\s*9').Count -ne 2) {
    throw 'Capture report and manifest are not both stamped with tap-diagnostic schema version 9.'
}
if ($service -notmatch 'ally-bindings-\{captureKind\}' -or
    $service -notmatch 'armoury-tap-evidence\.json' -or
    $service -notmatch 'native Armoury HID write tap') {
    throw 'Native tap bundles, evidence files or README text can still be mislabeled as ETW.'
}
if ($service -match '\betw\s*=\s*new' -or
    $service -notmatch 'kind = "armouryHidWriteTap"' -or
    $service -notmatch 'kind = "windowsUsbEtw"' -or
    $service -notmatch 'schemaDiscovery = session\.UsesArmouryTap') {
    throw 'Generated evidence can still label native tap output as ETW/schema discovery.'
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
    if ($workflow.IndexOf('test-armoury-tap-runtime.ps1', [StringComparison]::Ordinal) -lt 0) {
        throw 'A shipping workflow does not run the behavioral Armoury tap runtime test.'
    }
    if ($workflow.IndexOf('test-armoury-tap-embedded-resource.ps1', [StringComparison]::Ordinal) -lt 0) {
        throw 'A shipping workflow does not verify the packaged Armoury tap resource.'
    }
}
foreach ($requiredNativeBuildToken in @(
    'Visual Studio 18 2026',
    'Visual Studio 17 2022',
    'foreach ($generator in $generators)',
    'trying the next supported generator',
    '-A x64',
    'Release/AllyBindings.ArmouryTap.dll'
)) {
    if ($nativeBuildScript.IndexOf($requiredNativeBuildToken, [StringComparison]::Ordinal) -lt 0) {
        throw "The adaptive native build script is missing '$requiredNativeBuildToken'."
    }
}
foreach ($requiredEmbeddedTestToken in @(
    'AllyBindings.Windows.Native.AllyBindings.ArmouryTap.dll',
    'GetManifestResourceStream',
    'FixedTimeEquals',
    'Published single-file executable does not contain the native tap DLL bytes'
)) {
    if ($embeddedTapTest.IndexOf($requiredEmbeddedTestToken, [StringComparison]::Ordinal) -lt 0) {
        throw "The embedded native tap package test is missing '$requiredEmbeddedTestToken'."
    }
}
if ($buildWorkflow.IndexOf('build-armoury-tap.ps1', [StringComparison]::Ordinal) -lt 0 -or
    $packageScript.IndexOf('build-armoury-tap.ps1', [StringComparison]::Ordinal) -lt 0) {
    throw 'PR and release packaging do not share the adaptive native Armoury tap build script.'
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
    $service -notmatch 'Diagnostic metadata is never hardware-unlock evidence' -or
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
    $app -notmatch 'if \(_exiting\) return;' -or
    $app -notmatch 'blockedReason = _armouryCaptureTeardownUnconfirmed' -or
    $app -notmatch 'SetArmouryCaptureBlocked\(true, blockedReason\)' -or
    $app -notmatch 'OnSessionEnding\(SessionEndingCancelEventArgs e\)[\s\S]*_exiting = true;') {
    throw 'Exit/session-ending can race queued capture startup or blocked capture activation can still fail silently.'
}
if ($app -notmatch 'private void RequestArmouryCaptureCancellation\(\)' -or
    $app -notmatch '_mainWindow\.Dispatcher\.CheckAccess\(\)' -or
    $app -notmatch '_mainWindow\.Dispatcher\.Invoke\(_mainWindow\.CancelControllerDialog\)') {
    throw 'Capture cancellation can still touch WPF dialog state from a worker thread.'
}

if ($service -notmatch 'BoundedTextLineReader' -or $helper -notmatch 'BoundedTextLineReader' -or
    $boundedReader -notmatch 'Array\.IndexOf' -or $boundedReader -notmatch '_offset') {
    throw 'The ETW IPC protocol does not bound both command and response messages.'
}
foreach ($forbidden in @('TraceEventSession(sessionName,', 'data.EventData()', 'USBPcapCMD', 'logman.exe', 'tracerpt.exe', 'File.WriteAllBytes', 'stream.SetFeature(', 'WriteFeatureReport', 'IControllerBackend')) {
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

# ── Self-contained Armoury tap assertions ──

$tapProtocolPath = Join-Path $root 'src/AllyBindings.Core/ArmouryTapProtocol.cs'
$tapHelperPath = Join-Path $root 'src/AllyBindings.Windows/ArmouryTapCaptureHelper.cs'
$tapNativePath = Join-Path $root 'native/ArmouryTap/src/ArmouryTap.cpp'
$tapCmakePath = Join-Path $root 'native/ArmouryTap/CMakeLists.txt'
$tapSecurityDocPath = Join-Path $root 'docs/ARMOURY-TAP-SECURITY.md'
$tapUserGuidePath = Join-Path $root 'docs/ARMOURY-TAP-USER-GUIDE.md'

$tapProtocol = Get-Content -Raw -LiteralPath $tapProtocolPath
$tapHelper = Get-Content -Raw -LiteralPath $tapHelperPath
$tapNative = Get-Content -Raw -LiteralPath $tapNativePath
$tapCmake = Get-Content -Raw -LiteralPath $tapCmakePath
$tapSecurityDoc = Get-Content -Raw -LiteralPath $tapSecurityDocPath
$tapUserGuide = Get-Content -Raw -LiteralPath $tapUserGuidePath

# PID 1B6E must not be in the Ally rear-button allowlist
$hidDevicePath = Join-Path $root 'src/AllyBindings.Windows/AsusRearButtonHidDevice.cs'
$hidDevice = Get-Content -Raw -LiteralPath $hidDevicePath
if ($hidDevice -match '0x1B6E') {
    throw 'PID 0x1B6E (ProArt PZ13) is still in the ASUS rear-button allowlist.'
}

# Tap protocol constants
foreach ($required in @(
    'AsusVendorId = 0x0B05',
    'AllyProductId = 0x1B4C',
    'ReportId = 0x5A',
    'RearMappingCommand = 0xD1',
    'MinimumReportLength = 50',
    'MaximumReportLength = 64',
    'MaximumRecords = 256',
    'MaximumCandidateProcesses = 12',
    'CandidateHandshakeStepTimeout = TimeSpan.FromSeconds(5)',
    'CandidateRemoteCallTimeoutMilliseconds = 5_000',
    'CandidateWorstCaseStartupDuration',
    'CandidateWorstCaseStartupDuration * MaximumCandidateProcesses + TimeSpan.FromSeconds(60)',
    'WireRecordSize = 124',
    'WireMagic = 0x31544241',
    'WireVersion = 2',
    'ArmouryCrateSE.Service',
    'ArmouryCrate.Service',
    'ArmouryCrateSE',
    'ArmouryCrate.UserSessionHelper',
    'ArmouryCrateControlInterface',
    'ArmourySocketServer',
    'ArmourySwAgent',
    'ArmouryCrateKeyControl',
    'AsusOptimization')) {
    if ($tapProtocol.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The tap protocol contract is missing: $required"
    }
}

# Tap helper must authenticate, verify signatures, and fail closed
foreach ($required in @(
    'HelperArgument = "--armoury-tap-capture-helper"',
    'NativeResourceName',
    'WinVerifyTrust',
    'ASUSTeK COMPUTER INC.',
    'IsWow64Process2',
    'IsNativeAmd64',
    'HasAsusAuthenticodeSignature',
    'IsTrustedInstallPath',
    'HasReparseTraversal',
    'OpenParentImpersonationToken',
    'IsWritableByToken',
    'GetMaximumAllowedAccess',
    'AccessCheck',
    'DuplicateToken',
    'GetTrustedInstallRoot',
    'ImageHashMatches',
    'ImageLock',
    'MaximumCandidateProcesses = ArmouryTapProtocol.MaximumCandidateProcesses',
    'ArmouryTapProtocol.CandidateHandshakeStepTimeout',
    'ArmouryTapProtocol.CandidateRemoteCallTimeoutMilliseconds',
    'Allowlisted process observations:',
    'user-writable-image-or-parent',
    'asus-signature-rejected',
    'CandidateAttachmentCoordinator.AttachAvailableAsync',
    'DescribeSafeAttachRejection',
    'LockAndVerifyNativeDll',
    'IncrementalHash.CreateHash',
    'CryptographicOperations.FixedTimeEquals(actualHash, expectedHash)',
    'Revalidate',
    'StartTimeUtc',
    'ExecutablePath',
    'CryptographicOperations.FixedTimeEquals',
    'GetNamedPipeClientProcessId',
    'GetNamedPipeServerProcessId',
    'VerifyParentExecutableIdentity',
    'CreatePrivateExtractionDirectory',
    'CreateDirectoryW',
    'Environment.SpecialFolder.Windows',
    'FileSystemRights.ReadAndExecute',
    'Encoding.ASCII.GetBytes',
    'DeleteExtractionDirectory',
    'DirectorySecurity',
    'SetAccessRuleProtection',
    'PipeSecurity',
    'NetworkSid',
    'AccessControlType.Deny',
    'OpenProcess',
    'VirtualAllocEx',
    'WriteProcessMemory',
    'CreateRemoteThread',
    'FreeLibrary',
    'FindRemoteModule',
    'FindRemoteModuleByPathWithRetry',
    'GetModuleHandleExW',
    'LifecycleHandle',
    'helper={Environment.ProcessId}',
    'RevocationChecks = 1',
    'firstError == 18',
    'teardown remains retryable',
    'ReadExportRva',
    'ArmouryTapStop',
    'MaximumCaptureDuration',
    'TapUnavailableErrorCode',
    'TeardownUnconfirmedErrorCode',
    'TapTeardownUnconfirmedException')) {
    if ($tapHelper.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The tap helper is missing safety/lifecycle control: $required"
    }
}

foreach ($required in @(
    'attachedTargets.EnsureCapacity(candidates.Count)',
    'attachedTargets.Add(attachedTarget)',
    'when (cancellationToken.IsCancellationRequested)',
    'when (!requiresFailClosedAbort(ex))',
    'CandidateAttachmentRejection')) {
    if ($candidateAttachmentCoordinator.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The per-candidate attachment coordinator is missing fail-closed lifecycle behavior: $required"
    }
}

$tapStartIndex = $service.IndexOf('public async Task<ArmouryCaptureSession> StartAsync(', [StringComparison]::Ordinal)
$fallbackStartIndex = $service.IndexOf('public Task<ArmouryCaptureSession> StartMetadataFallbackAsync(', [StringComparison]::Ordinal)
if ($tapStartIndex -lt 0 -or $fallbackStartIndex -le $tapStartIndex) {
    throw 'The native tap and explicit metadata fallback entry points are not independently defined.'
}
$tapStartContract = $service.Substring($tapStartIndex, $fallbackStartIndex - $tapStartIndex)
if ($tapStartContract.IndexOf('ArmouryEtwCaptureHelper.HelperArgument', [StringComparison]::Ordinal) -ge 0) {
    throw 'The native tap start path still auto-starts the system-wide ETW fallback before explicit consent.'
}
foreach ($required in @(
    'StartMetadataFallbackAsync',
    'ArmouryTapCaptureStartTimeout = ArmouryTapProtocol.CaptureStartupTimeout',
    'isTapHelper ? ArmouryTapCaptureStartTimeout : CaptureStartTimeout')) {
    if ($service.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The capture service is missing explicit-fallback/deadline control: $required"
    }
}
$fallbackDisclosureIndex = $app.IndexOf('No ETW session has started. The optional metadata-only Windows ETW fallback', [StringComparison]::Ordinal)
$fallbackLaunchIndex = $app.IndexOf('StartMetadataFallbackAsync', [StringComparison]::Ordinal)
if ($fallbackDisclosureIndex -lt 0 -or $fallbackLaunchIndex -le $fallbackDisclosureIndex -or
    $app.IndexOf('primaryLabel: "Start ETW fallback"', [StringComparison]::Ordinal) -lt 0) {
    throw 'The UI does not obtain explicit ETW fallback consent before launching the metadata helper.'
}

$tapRecordStart = $tapProtocol.IndexOf('public sealed record ArmouryTapRecord(', [StringComparison]::Ordinal)
$tapRecordEnd = $tapProtocol.IndexOf(');', $tapRecordStart, [StringComparison]::Ordinal)
if ($tapRecordStart -lt 0 -or $tapRecordEnd -le $tapRecordStart) {
    throw 'Could not isolate the exported ArmouryTapRecord contract.'
}
$tapRecordContract = $tapProtocol.Substring($tapRecordStart, $tapRecordEnd - $tapRecordStart)
foreach ($forbidden in @('ProcessId', 'PerformanceCounterTimestamp', 'ExecutablePath', 'Qpc')) {
    if ($tapRecordContract.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "The exported tap record leaks an internal identity/time field: $forbidden"
    }
}
foreach ($required in @('ProcessName', 'Phase', 'Ordinal')) {
    if ($tapRecordContract.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The exported tap record is missing sanitized attribution: $required"
    }
}

# Native DLL must hook the right APIs, preserve return/LastError, and drain callbacks
foreach ($required in @(
    'HidD_SetFeature',
    'HidD_SetOutputReport',
    'WriteFile',
    'DeviceIoControl',
    'kIoctlHidSetFeature = 0x000B0191',
    'kIoctlHidSetOutputReport = 0x000B0195',
    'g_hidWrapperDepth',
    'g_internalIoDepth',
    'HandleClassification',
    'CompareObjectHandles',
    'IsKnownTargetHandle',
    'RememberTargetHandle',
    'ReleaseValidatedHandles',
    'g_unvalidatedWriteHandle',
    'AttributeReadFailure',
    'BuildSummaryRecord',
    'MH_Initialize',
    'MH_CreateHook',
    'MH_QueueEnableHook',
    'MH_ApplyQueued',
    'MH_DisableHook',
    'MH_Uninitialize',
    'DisableHooksAndDrain',
    'transportFailure',
    'g_helperProcess',
    'helperPrefix',
    'FreeLibraryAndExitThread',
    'ReadProcessMemory',
    'SetLastError(incomingError)',
    'SetLastError(error)',
    'g_activeCallbacks',
    'CallbackLease',
    'g_droppedRecords',
    'ArmouryTapStop',
    'kVendor',
    'kProduct',
    '0x5A',
    'kRearMappingCommand = 0xD1',
    'std::string text',
    'kMinReport',
    'kMaxReport',
    'kQueueCapacity')) {
    if ($tapNative.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The native tap DLL is missing safety requirement: $required"
    }
}
$lengthFilter = $tapNative.IndexOf('if (length < kMinReport)', [StringComparison]::Ordinal)
$safePrefixFilter = $tapNative.IndexOf('if (copy[0] != 0x5A)', [StringComparison]::Ordinal)
$handleValidation = $tapNative.IndexOf('switch (ClassifyHandle(handle))', [StringComparison]::Ordinal)
if ($lengthFilter -lt 0 -or $safePrefixFilter -lt $lengthFilter -or $handleValidation -lt $safePrefixFilter) {
    throw 'The native tap can probe arbitrary WriteFile handles before cheap length and safe-prefix filtering.'
}
if ($tapNative -notmatch 'if \(api == Api::KernelBaseWriteFile\)[\s\S]{0,250}!IsKnownTargetHandle\(handle\)[\s\S]{0,350}else \{[\s\S]{0,100}switch \(ClassifyHandle\(handle\)\)' -or
    $tapNative -notmatch 'if \(!ReleaseValidatedHandles\(\)\) return false;') {
    throw 'WriteFile retention is not restricted to object-identical HID-validated handles with confirmed owned-handle release.'
}
if ($tapNative -match 'const auto summary = BuildSummaryRecord\(\);[\s\S]{0,300}FlushFileBuffers\(g_pipe\)' -or
    $tapNative -match 'Api::Overflow' -or $tapHelper -match 'OverflowRecordApi' -or
    $tapHelper -notmatch '_receivedRecordCount') {
    throw 'Wire-v2 terminal-summary/drop reconciliation is not authoritative and nonblocking.'
}

foreach ($required in @(
    'ArmouryTapTeardownBlockedSinceUtc',
    'ArmouryTapTeardownBootIdentifier',
    'TapTeardownBarrierRecovery.Evaluate',
    'TapTeardownBarrierRecoveryDecision.EstablishBootBaseline',
    'restart Windows once more to prove every affected process exited',
    'NtQuerySystemInformation',
    'Restart Windows; restarting Ally Bindings alone is not sufficient',
    '_armouryCaptureTeardownUnconfirmed = true')) {
    if ($app.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The app-wide persisted tap teardown barrier is missing requirement: $required"
    }
}
foreach ($required in @('nativeWritesAllowed', '_armouryCaptureBarrierPersistenceFailed', 'Ordinary app exit is blocked')) {
    if ($app.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The persisted teardown barrier does not block startup/exit path: $required"
    }
}
if ($app.IndexOf('Environment.TickCount64', [StringComparison]::Ordinal) -ge 0 -or
    $app.IndexOf('ArmouryTapTeardownBootStartedUtc', [StringComparison]::Ordinal) -ge 0) {
    throw 'The teardown barrier still infers a reboot from wall-clock or estimated boot timestamps.'
}
if ($app -notmatch '_operationGate\.WaitAsync\(cancellationToken\)[\s\S]{0,700}armedBarrierConfiguration = Configuration with[\s\S]{0,900}ArmTapTeardownBarrierAsync\(armedBarrierConfiguration\)[\s\S]{0,400}Configuration = armedBarrierConfiguration[\s\S]{0,400}_operationGate\.Release\(\)[\s\S]{0,400}ThrowIfCancellationRequested\(\)[\s\S]{0,500}captureService\.StartAsync') {
    throw 'The native tap can start before its crash-durable write-ahead barrier is persisted.'
}
if ($app -notmatch 'captureTeardownFailure is null && armedConfiguration is not null[\s\S]{0,1200}ArmouryTapTeardownBlockedSinceUtc = null[\s\S]{0,700}ClearTapTeardownBarrierAsync\(clearedConfiguration\)') {
    throw 'The native tap barrier can clear before teardown succeeds or without using its explicit lifecycle operation.'
}
if ($app -notmatch 'EstablishTapTeardownBootBaselineAsync\(Configuration\)') {
    throw 'A legacy tap barrier boot baseline is not persisted through its explicit sticky-barrier lifecycle operation.'
}
foreach ($required in @(
    '.tap-barrier',
    'WriteTapBarrierSentinelAsync',
    'WriteConfigurationFileAtomicallyAsync($"{_path}.bak"',
    'File.Delete(_tapBarrierPath)')) {
    if ($profileStore.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The crash-atomic tap barrier sentinel is missing requirement: $required"
    }
}
if ([regex]::Matches($profileStore, 'EnsureUncertainRecoveryIsBlocked\(recovered\.Configuration\)').Count -lt 2) {
    throw 'Missing and corrupt primary recovery can trust a stale unarmed legacy backup instead of failing closed.'
}
foreach ($required in @(
    'LoadLibraryExW(L"hid.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32)',
    'g_hookFailureStage',
    'failure.apiResult = g_hookFailureStage',
    'tap-pipe-connect-timeout',
    'tap-ready-timeout',
    'tap-hook-stage-',
    'S:(ML;;NW;;;ME)')) {
    if ($tapNative.IndexOf($required, [StringComparison]::Ordinal) -lt 0 -and
        $tapHelper.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The native tap startup diagnostics are missing requirement: $required"
    }
}
foreach ($required in @(
    'const bool hooksRemoved = DisableHooksAndDrain();',
    'return hooksRemoved ? 4 : 6;',
    'switch (workerExitCode)',
    'Exit 6 and every unknown status are unsafe to unload.')) {
    if ($tapNative.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Native hook-install rollback is missing requirement: $required"
    }
}
if ($tapHelper -match 'configLock\s*=\s*new FileStream\(configPath,\s*FileMode\.CreateNew,\s*FileAccess\.Write') {
    throw 'The tap still holds its config write handle across injection, blocking the native read handshake.'
}
if ($tapHelper -notmatch 'configWriter = new FileStream\([\s\S]{0,500}FileAccess\.Write, FileShare\.None[\s\S]{0,700}configLock = new FileStream\([\s\S]{0,200}FileAccess\.Read, FileShare\.Read[\s\S]{0,300}remoteModule = Inject') {
    throw 'The tap config is not closed after durable write and reopened under a compatible read-only lock before injection.'
}
if ($app -notmatch 'if \(_armouryCaptureBarrierPersistenceFailed\)[\s\S]{0,1400}_exiting = false;[\s\S]{0,300}OpenMainWindow\(\);[\s\S]{0,200}return;') {
    throw 'The persistence-failure Stay open path can permanently leak the app exit-intent latch.'
}
$tapRuntime = Get-Content -Raw -LiteralPath (Join-Path $root 'scripts/test-armoury-tap-runtime.ps1')
if ($tapRuntime.IndexOf("LoadLibraryW('hid.dll')", [StringComparison]::Ordinal) -ge 0) {
    throw 'The runtime test still masks production hid.dll loading by preloading it.'
}
if ($tapRuntime.IndexOf('tap PE dependency did not cause Windows to map hid.dll', [StringComparison]::Ordinal) -lt 0) {
    throw 'The runtime test does not prove loader-owned hid.dll dependency mapping.'
}
foreach ($required in @(
    '$stopConfirmed',
    'if ($stopConfirmed)',
    'DeviceIoControlSetFeatureCallCount',
    'DecodeDiagnosticSummaryBytes',
    'UnvalidatedWriteHandleCount',
    'bounded 0x5A regular-file WriteFile probe')) {
    if ($tapRuntime.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The runtime test is missing fail-closed teardown or native-to-managed summary coverage: $required"
    }
}
if ($app -notmatch '!session\.NativeTeardownConfirmed' -or
    $app -match 'CompleteAsync\(session, cancellationToken\);\s*cancellationToken\.ThrowIfCancellationRequested' -or
    $service -notmatch 'EvidenceInvalidCleanupConfirmedErrorCode' -or
    $service -notmatch 'expectedExitCode:\s*1,[\s\S]{0,100}cancellationToken:\s*CancellationToken\.None' -or
    $service -notmatch 'expectedExitCode:\s*0,[\s\S]{0,100}cancellationToken:\s*CancellationToken\.None' -or
    $service -notmatch 'helper\.ExitCode != expectedExitCode' -or
    $service -notmatch 'session\.MarkNativeTeardownConfirmed\(\)' -or
    $service -notmatch 'session\.MarkCompletionCommitted\(\)') {
    throw 'Terminal outcomes, cleanup-confirmed protocol failures, or late cancellation can still arm the reboot barrier or delete committed evidence.'
}
foreach ($required in @(
    'id: publish',
    'Fail-closed public release auditor',
    'public-release-auditor',
    'needs.release.result',
    'for ($attempt = 1; $attempt -le 3; $attempt++)',
    'Could not determine or enforce fail-closed release state')) {
    if ($releaseWorkflow.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The release publication boundary is missing fail-closed control: $required"
    }
}

# CMake must produce x64-only DLL with security mitigations
foreach ($required in @('FATAL_ERROR', 'MultiThreaded', '/W4', '/WX', '/WX-', '/guard:cf', '/DYNAMICBASE', '/HIGHENTROPYVA', '/NXCOMPAT', '/CETCOMPAT', '/Brepro', 'AllyBindings.ArmouryTap')) {
    if ($tapCmake.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The native CMake build is missing requirement: $required"
    }
}

# Security contract and user guide must exist with key invariants
foreach ($required in @(
    'hardwareWriteAttemptedByAllyBindings: false',
    'hardwareUnlockEvidence: false',
    'reviewRequired: true',
    'driverInstalled: false',
    'externalCaptureToolRequired: false',
    'rawSystemTraceWritten: false')) {
    if ($tapSecurityDoc.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The tap security contract is missing invariant: $required"
    }
}
if ($tapUserGuidePath -and (Test-Path -LiteralPath $tapUserGuidePath) -eq $false) {
    throw 'The tap user guide document is missing.'
}
$readmePath = Join-Path $root 'README.md'
$xamlPath = Join-Path $root 'src/AllyBindings.Windows/MainWindow.xaml'
$readme = Get-Content -Raw -LiteralPath $readmePath
$xaml = Get-Content -Raw -LiteralPath $xamlPath

foreach ($required in @(
    'one or more verified ASUS Armoury candidate processes',
    'enumerates running processes matching nine exact allowlisted executable names',
    'Capture is rejected if more than twelve candidates pass path, signature and identity verification',
    'may temporarily inject into each verified candidate')) {
    if ($app.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The in-app consent disclosure understates process enumeration or injection scope: $required"
    }
}
foreach ($required in @(
    'enumerates running processes matching nine exact allowlisted ASUS Armoury executable names',
    'Capture is rejected if more than twelve candidates pass path, signature and identity verification',
    'unload from every attached candidate')) {
    if ($tapUserGuide.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The packaged user guide understates process enumeration or injection scope: $required"
    }
}
foreach ($required in @(
    'enumerates running processes matching nine exact allowlisted ASUS Armoury executable names',
    'capture is rejected if more than twelve candidates pass verification')) {
    if ($readme.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The packaged README understates process enumeration or injection scope: $required"
    }
}
foreach ($required in @(
    'one or more verified ASUS Armoury candidate processes',
    'up to twelve from nine exact allowlisted names')) {
    if ($xaml.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The capture panel XAML understates multi-process injection scope: $required"
    }
}
$stalePhrases = @(
    @{File='app'; Text='the confirmed ASUS Armoury process'},
    @{File='app'; Text='examines no more than twelve processes'},
    @{File='tapUserGuide'; Text='only into the confirmed ASUS process'},
    @{File='tapUserGuide'; Text='up to twelve processes drawn from nine exact allowlisted executable names'},
    @{File='readme'; Text='inside an exact ASUS-signed Armoury process'},
    @{File='readme'; Text='up to twelve selected from nine exact allowlisted executable names'},
    @{File='xaml'; Text='the confirmed ASUS process'}
)
foreach ($entry in $stalePhrases) {
    $content = switch ($entry.File) { 'app' { $app } 'tapUserGuide' { $tapUserGuide } 'readme' { $readme } 'xaml' { $xaml } }
    if ($content.IndexOf($entry.Text, [StringComparison]::Ordinal) -ge 0) {
        throw "A stale singular-process consent claim remains in user-facing material: $($entry.File) contains '$($entry.Text)'"
    }
}

# App startup must recognize tap helper arguments before the single-instance mutex
if ($app.IndexOf('ArmouryTapCaptureHelper.TryParseArguments', [StringComparison]::Ordinal) -lt 0 -or
    $app.IndexOf('ArmouryTapCaptureHelper.TryParseArguments', [StringComparison]::Ordinal) -gt
    $app.IndexOf('new Mutex(', [StringComparison]::Ordinal)) {
    throw 'The tap helper is not recognized before the single-instance mutex.'
}

# Capture service must try the tap first; the app obtains explicit consent before starting ETW fallback.
if ($service -notmatch 'ArmouryTapCaptureHelper\.HelperArgument' -or
    $service -notmatch 'ArmouryEtwCaptureHelper\.HelperArgument' -or
    $app -notmatch 'catch \(ArmouryTapUnavailableException(?:\s+\w+)?\)' -or
    $service -notmatch 'StartMetadataFallbackAsync' -or
    $service -notmatch 'TapUnavailableReason' -or
    $app -notmatch 'Native Armoury tap unavailable' -or
    $app -notmatch 'Start ETW fallback' -or
    $service -notmatch 'TeardownUnconfirmedErrorCode' -or
    $service -notmatch 'ArmouryCaptureTeardownException') {
    throw 'The capture flow does not preserve tap-first startup and explicit ETW fallback consent.'
}
if ([regex]::Matches($service, 'TeardownUnconfirmedErrorCode').Count -lt 2 -or
    [regex]::Matches($service, 'throw new ArmouryCaptureTeardownException').Count -lt 2) {
    throw 'Structured teardown-unconfirmed status is not promoted during both startup and completion.'
}
if ($app -notmatch 'ex is ArmouryCaptureTeardownException' -or
    $app -notmatch 'captureTeardownFailure = ex' -or
    $app -notmatch '_armouryCaptureTeardownUnconfirmed = true') {
    throw 'The app-wide reset/write barrier is not latched after structured tap teardown failure.'
}
if ($service -notmatch 'isTapHelper' -or
    $service -notmatch 'ex is ArmouryTapUnavailableException or ArmouryCaptureTeardownException' -or
    $service -notmatch 'UsesArmouryTap' -or
    $service -notmatch 'HelperProcess\.ExitCode != 2' -or
    $app -notmatch 'session\?\.UsesArmouryTap == true') {
    throw 'An unstructured tap-helper crash can bypass the app-wide teardown barrier.'
}
$cleanupIndex = $tapHelper.IndexOf('await CleanupAsync().ConfigureAwait(false);', [StringComparison]::Ordinal)
$resultIndex = $tapHelper.IndexOf('new EtwPipeEnvelope("result"', [StringComparison]::Ordinal)
if ($cleanupIndex -lt 0 -or $resultIndex -lt 0 -or $cleanupIndex -gt $resultIndex) {
    throw 'The tap helper can publish a successful result before hook/file cleanup is confirmed.'
}

# Manifest must distinguish tap vs ETW source
if ($service -notmatch 'Self-contained ASUS-signed-process user-mode HID write tap' -or
    $service -notmatch 'Windows built-in USB ETW real-time FullDataBusTrace session') {
    throw 'The capture manifest does not distinguish tap vs ETW evidence source.'
}

Write-Output 'Integrated ETW capture safety and privacy assertions passed.'
