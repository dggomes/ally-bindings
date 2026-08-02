$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$xamlPath = Join-Path $root 'src/AllyBindings.Windows/MainWindow.xaml'
$appXamlPath = Join-Path $root 'src/AllyBindings.Windows/App.xaml'
$windowCodePath = Join-Path $root 'src/AllyBindings.Windows/MainWindow.xaml.cs'
$appCodePath = Join-Path $root 'src/AllyBindings.Windows/App.xaml.cs'
$projectPath = Join-Path $root 'src/AllyBindings.Windows/AllyBindings.Windows.csproj'
$iconPath = Join-Path $root 'src/AllyBindings.Windows/Assets/AllyBindings.ico'
$pngPath = Join-Path $root 'src/AllyBindings.Windows/Assets/AllyBindings.png'

$xaml = Get-Content -Raw -LiteralPath $xamlPath
$appXaml = Get-Content -Raw -LiteralPath $appXamlPath
$windowCode = Get-Content -Raw -LiteralPath $windowCodePath
$appCode = Get-Content -Raw -LiteralPath $appCodePath
$project = Get-Content -Raw -LiteralPath $projectPath

# Parse both documents so malformed XAML fails before a Windows build.
[xml]$xaml | Out-Null
[xml]$appXaml | Out-Null

foreach ($requiredText in @(
    'Capture &amp; update',
    'AutomationProperties.AutomationId="NavigationProfiles"',
    'AutomationProperties.AutomationId="NavigationController"',
    'AutomationProperties.AutomationId="NavigationShortcut"',
    'AutomationProperties.AutomationId="NavigationCaptureUpdate"',
    'Capture Armoury M1/M2',
    'x:Name="ArmouryCaptureButton"',
    'Content="Start capture"',
    'Content="Update app"',
    'Content="Update app now"',
    'Controller mapping',
    'ItemsSource="{Binding LeftBindings}"',
    'ItemsSource="{Binding RightBindings}"',
    'x:Name="BindingPickerOverlay"',
    'x:Name="NameKeyboardOverlay"',
    'x:Name="ControllerDialogOverlay"',
    'Rename profile with controller keyboard',
    'D-pad chooses · A confirms · B cancels',
    'LB / RB  Sections',
    'A Select   B Back'
)) {
    if ($xaml.IndexOf($requiredText, [StringComparison]::Ordinal) -lt 0) {
        throw "Controller-first UI is missing required surface: $requiredText"
    }
}
if ($xaml.IndexOf('<DataGrid', [StringComparison]::Ordinal) -ge 0) {
    throw 'The old spreadsheet-style binding editor is still present.'
}
if ($appXaml -notmatch '<Setter Property="MinHeight" Value="48"') {
    throw 'The global touch target minimum is below 48 device-independent pixels.'
}
foreach ($command in @('MoveUp', 'MoveDown', 'PreviousSection', 'NextSection', 'Activate', 'Back', 'Save', 'Apply')) {
    if ($windowCode.IndexOf("ControllerUiCommand.$command", [StringComparison]::Ordinal) -lt 0) {
        throw "Controller UI command is not handled: $command"
    }
}
if ($appCode -notmatch 'SignalExistingInstance\(\)' -or
    $appCode -notmatch 'PipeOptions\.CurrentUserOnly' -or
    $appCode -notmatch 'StartActivationListener\(\)' -or
    $appCode -notmatch 'HandleControllerInput\(snapshot\)') {
    throw 'Window activation or controller navigation is not wired into the application shell.'
}
if ($project -notmatch '<ApplicationIcon>Assets\\AllyBindings\.ico</ApplicationIcon>' -or
    $appCode -notmatch 'ExtractAssociatedIcon' -or
    -not (Test-Path -LiteralPath $iconPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $pngPath -PathType Leaf)) {
    throw 'Branded application and tray icon resources are incomplete.'
}

Write-Output 'Controller-first UI discoverability, touch target, navigation, activation, and icon assertions passed.'
