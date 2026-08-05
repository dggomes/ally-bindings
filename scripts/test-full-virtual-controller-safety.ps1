$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$core = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.Core/ControllerRecoveryGestureStateMachine.cs')
$pipeline = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.Core/RealtimeControllerPipeline.cs')
$hook = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.Windows/F11F12PaddleHook.cs')
$backend = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.Windows/VirtualControllerBackend.cs')
$xinput = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.Windows/XInputMonitor.cs')
$app = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.Windows/App.xaml.cs')
$config = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.Core/Configuration.cs')
$latch = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.Core/VirtualRemappingSafetyLatch.cs')
$project = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/AllyBindings.Windows/AllyBindings.Windows.csproj')
$runbook = Get-Content -Raw -LiteralPath (Join-Path $repo 'docs/FULL-VIRTUAL-CONTROLLER-VALIDATION.md')
$notices = Get-Content -Raw -LiteralPath (Join-Path $repo 'THIRD-PARTY-NOTICES.md')

function Require([string]$Text, [string]$Needle, [string]$Failure) {
    if ($Text.IndexOf($Needle, [StringComparison]::Ordinal) -lt 0) { throw $Failure }
}

foreach ($needle in @(
    'ChordHoldMilliseconds = 750',
    'LeftTriggerHoldMilliseconds = 1250',
    'public bool IsConsumingInput',
    'StickNeutralDeadzone',
    'WaitingForNeutral'
)) {
    Require $core $needle "Recovery recognizer is missing required contract: $needle"
}

foreach ($needle in @(
    'MappingEngine.Apply',
    'RearPaddleOverlay.Apply',
    'SetProfile',
    'SetRearPaddle',
    'ProcessPhysical'
)) {
    Require $pipeline $needle "Realtime mapping pipeline is incomplete: $needle"
}

foreach ($needle in @(
    'BlockingCollection<RearPaddleKeyTransition>',
    'LlkLowerIntegrityInjected',
    'LlkInjected',
    '_events.TryAdd',
    'return new IntPtr(1)',
    'ControllerButton.M1',
    'ControllerButton.M2'
)) {
    Require $hook $needle "Paddle hook is missing a safety/property assertion: $needle"
}

foreach ($needle in @(
    'AutoSubmitReport = false',
    'Xbox360Slider.LeftTrigger',
    'Xbox360Slider.RightTrigger',
    'Xbox360Axis.LeftThumbX',
    'Xbox360Axis.LeftThumbY',
    'Xbox360Axis.RightThumbX',
    'Xbox360Axis.RightThumbY',
    'SubmitReport()',
    'NeutralizeAndDisconnectLocked'
)) {
    Require $backend $needle "Virtual backend is not a complete atomic mirror: $needle"
}

$processIndex = $app.IndexOf('_virtualBackend?.ProcessSnapshot(snapshot)', [StringComparison]::Ordinal)
$recoveryIndex = $app.IndexOf('_recoveryGesture.Process(snapshot', [StringComparison]::Ordinal)
$uiIndex = $app.IndexOf('_mainWindow.HandleControllerInput(snapshot)', [StringComparison]::Ordinal)
if ($recoveryIndex -lt 0 -or $processIndex -le $recoveryIndex -or $uiIndex -le $processIndex) {
    throw 'Runtime order must be fixed recovery recognizer, physical mirror, then controller UI/profile routing.'
}
foreach ($needle in @(
    'FindFirstConnectedIndex',
    'A physical XInput index must be pinned before virtual output is created.',
    '_recoveryGesture.IsConsumingInput',
    'virtual-remapping-disabled',
    'TryClearVirtualRecoveryLatchAfterEnable()',
    '_virtualSafetyPending',
    'SignalVirtualSafetyEvent(reason)',
    'EmergencyStopVirtualControllerAsync',
    'StopPaddleHook()',
    'Pinned physical controller disconnected',
    'Stopwatch.GetElapsedTime'
)) {
    Require $app $needle "Application lifecycle is missing a virtual-controller safety seam: $needle"
}
$saveStart = $app.IndexOf('public async Task<bool> SaveEditorAsync', [StringComparison]::Ordinal)
$saveEnd = $app.IndexOf('public async Task ApplyProfileAsync', $saveStart, [StringComparison]::Ordinal)
$saveCode = $app.Substring($saveStart, $saveEnd - $saveStart)
$pinIndex = $saveCode.IndexOf('_controllerMonitor.SetPreferredIndex(pinnedPhysicalIndex)', [StringComparison]::Ordinal)
$connectIndex = $saveCode.IndexOf('replacementStatus = await ReplaceBackendAsync(', [StringComparison]::Ordinal)
if ($pinIndex -lt 0 -or $connectIndex -le $pinIndex) {
    throw 'The live XInput monitor must be pinned before the replacement ViGEm target connects.'
}
Require $saveCode 'var reapplied = await _backend.ApplyAsync(activeProfile);' 'Saving edits to the active profile does not reapply the new revision.'
Require $xinput 'private readonly System.Threading.Timer _safetyTimer;' 'Controller recovery still depends entirely on the WPF dispatcher.'
Require $xinput 'SafetySnapshotReceived?.Invoke' 'The independent XInput safety timer is not connected to recovery.'
Require $app '_controllerMonitor.SafetySnapshotReceived += RecoverySnapshotReceived;' 'The app does not subscribe recovery to the independent XInput safety timer.'
Require $latch 'File.Move(temporaryPath, _path, overwrite: true)' 'The durable virtual-remapping latch is not atomically replaced.'
Require $hook 'all physical' 'The global F11/F12 interception boundary is not documented in code.'

Require $config 'CurrentSchemaVersion = 4' 'Virtual-controller configuration was not schema-versioned.'
Require $config 'EnableVirtualControllerRemapping' 'Virtual-controller opt-in is missing from configuration.'
Require $project 'Nefarius.ViGEm.Client' 'Windows project is missing the pinned ViGEm client dependency.'
Require $notices 'Nefarius.ViGEm.Client 1.21.256' 'Third-party notices omit the ViGEm client dependency.'
Require $runbook 'Do **not** install or configure HidHide as a workaround.' 'Physical validation runbook does not block unsafe persistent hiding.'
Require $runbook 'Hold **View + Menu** for at least 750 ms.' 'Physical validation runbook omits controller-only recovery.'
Require $runbook 'requires every verdict to pass' 'The runbook does not require safe physical/virtual coexistence.'

$runtimeSources = @(
    (Join-Path $repo 'src/AllyBindings.Windows/App.xaml.cs'),
    (Join-Path $repo 'src/AllyBindings.Windows/VirtualControllerBackend.cs'),
    (Join-Path $repo 'src/AllyBindings.Windows/F11F12PaddleHook.cs')
)
foreach ($source in $runtimeSources) {
    $text = Get-Content -Raw -LiteralPath $source
    if ($text -match '(?i)HidHide|SESSION_BLACKLIST|ADD_BLACKLIST|RegisterHotKey|Ctrl\+Alt\+F12') {
        throw "Unsafe hiding or keyboard-recovery primitive entered the runtime source: $source"
    }
}
if (Test-Path -LiteralPath (Join-Path $repo 'src/AllyBindings.Windows/GlobalPanicHotKey.cs')) {
    throw 'The keyboard-only global panic hotkey source still exists.'
}

Write-Output 'Full virtual-controller mirror, controller-only recovery, fail-open topology, and packaging assertions passed.'
