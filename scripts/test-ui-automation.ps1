param(
    [string]$ExecutablePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/AllyBindings-win-x64/AllyBindings.exe')
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) {
    Write-Output 'WPF UI automation smoke test skipped: Windows is required.'
    exit 0
}
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw "Packaged executable is missing: $ExecutablePath"
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-ElementContaining {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [System.Windows.Automation.ControlType]$ControlType = $null
    )
    $elements = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $elements) {
        if ($ControlType -and $element.Current.ControlType -ne $ControlType) { continue }
        if ($element.Current.Name.IndexOf($Name, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $element }
    }
    return $null
}

function Wait-ElementContaining {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [System.Windows.Automation.ControlType]$ControlType = $null,
        [int]$TimeoutSeconds = 8
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $match = Find-ElementContaining -Root $Root -Name $Name -ControlType $ControlType
        if ($match) { return $match }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "UI element was not discoverable: $Name"
}

function Wait-ElementById {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [int]$TimeoutSeconds = 8
    )
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $match = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($match) { return $match }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    Write-Host "Automation tree while waiting for $AutomationId`:"
    $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        ForEach-Object { Write-Host "$($_.Current.ControlType.ProgrammaticName) | id=$($_.Current.AutomationId) | name=$($_.Current.Name)" }
    throw "UI element was not discoverable by AutomationId: $AutomationId"
}

$configRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'AllyBindings'
$configPath = Join-Path $configRoot 'config.json'
$hadConfig = Test-Path -LiteralPath $configPath -PathType Leaf
$savedConfig = if ($hadConfig) { [IO.File]::ReadAllBytes($configPath) } else { $null }
$process = $null
try {
    New-Item -ItemType Directory -Path $configRoot -Force | Out-Null
    $sample = Get-Content -Raw -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'examples/config.sample.json')
    $sample = $sample -replace '"activeProfileId": "default"', '"activeProfileId": "elden-ring"'
    [IO.File]::WriteAllText($configPath, $sample, [Text.UTF8Encoding]::new($false))

    $process = Start-Process -FilePath $ExecutablePath -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and -not $process.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if ($process.HasExited) { throw "Ally Bindings exited during UI startup with code $($process.ExitCode)." }
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Normal launch did not show the main window.' }

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $transform = [System.Windows.Automation.TransformPattern]$root.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    if ($transform.Current.CanResize) {
        $transform.Resize(900, 600)
        Start-Sleep -Milliseconds 250
    }
    Wait-ElementContaining -Root $root -Name 'Check for and install Ally Bindings updates' -ControlType ([System.Windows.Automation.ControlType]::Button) | Out-Null

    $maintenance = Wait-ElementById -Root $root -AutomationId 'NavigationCaptureUpdate'
    $selection = [System.Windows.Automation.SelectionItemPattern]$maintenance.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Start-Sleep -Milliseconds 300
    $capture = Wait-ElementById -Root $root -AutomationId 'ArmouryCaptureButton'
    Wait-ElementContaining -Root $root -Name 'Check for and install updates now' -ControlType ([System.Windows.Automation.ControlType]::Button) | Out-Null
    $invoke = [System.Windows.Automation.InvokePattern]$capture.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    $cancelDialog = Wait-ElementContaining -Root $root -Name 'B  Cancel' -ControlType ([System.Windows.Automation.ControlType]::Button)
    if ($maintenance.Current.IsEnabled) { throw 'Workspace navigation remained enabled behind a controller dialog.' }
    $invoke = [System.Windows.Automation.InvokePattern]$cancelDialog.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    Start-Sleep -Milliseconds 250

    $controller = Wait-ElementById -Root $root -AutomationId 'NavigationController'
    $selection = [System.Windows.Automation.SelectionItemPattern]$controller.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Start-Sleep -Milliseconds 300

    $leftBumper = Wait-ElementContaining -Root $root -Name 'Left bumper' -ControlType ([System.Windows.Automation.ControlType]::Button)
    if (-not $leftBumper.Current.IsEnabled) { throw 'A user-profile visual mapping button is unexpectedly disabled.' }
    $invoke = [System.Windows.Automation.InvokePattern]$leftBumper.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    Wait-ElementContaining -Root $root -Name 'Binding output choices' -ControlType ([System.Windows.Automation.ControlType]::List) | Out-Null
    if ($controller.Current.IsEnabled) { throw 'Workspace navigation remained enabled behind the binding picker.' }
    $cancelPicker = Wait-ElementContaining -Root $root -Name 'B  Cancel' -ControlType ([System.Windows.Automation.ControlType]::Button)
    $invoke = [System.Windows.Automation.InvokePattern]$cancelPicker.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()

    $profiles = Wait-ElementById -Root $root -AutomationId 'NavigationProfiles'
    $selection = [System.Windows.Automation.SelectionItemPattern]$profiles.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Start-Sleep -Milliseconds 250
    $rename = Wait-ElementContaining -Root $root -Name 'Rename profile with controller keyboard' -ControlType ([System.Windows.Automation.ControlType]::Button)
    $invoke = [System.Windows.Automation.InvokePattern]$rename.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    Wait-ElementContaining -Root $root -Name 'A  Done' -ControlType ([System.Windows.Automation.ControlType]::Button) | Out-Null
    $windowBounds = $root.Current.BoundingRectangle
    $visibleEnabledButtons = @($root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button))) | Where-Object {
            $_.Current.IsEnabled -and -not $_.Current.IsOffscreen
        })
    if ($visibleEnabledButtons.Count -lt 30) { throw "Controller keyboard exposed only $($visibleEnabledButtons.Count) usable buttons at 900x600." }
    foreach ($button in $visibleEnabledButtons) {
        $bounds = $button.Current.BoundingRectangle
        if ($bounds.Left -lt $windowBounds.Left - 1 -or $bounds.Top -lt $windowBounds.Top - 1 -or
            $bounds.Right -gt $windowBounds.Right + 1 -or $bounds.Bottom -gt $windowBounds.Bottom + 1) {
            throw "Controller keyboard button '$($button.Current.Name)' overflows the 900x600 window."
        }
    }
}
finally {
    if ($process) {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    if ($hadConfig) {
        [IO.File]::WriteAllBytes($configPath, $savedConfig)
    }
    else {
        Remove-Item -LiteralPath $configPath -Force -ErrorAction SilentlyContinue
    }
}

Write-Output 'WPF launch, section navigation, manual update, capture discoverability, and visual mapping automation passed.'
