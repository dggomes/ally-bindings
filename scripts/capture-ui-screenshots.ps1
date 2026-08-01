param(
    [string]$ExecutablePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/AllyBindings-win-x64/AllyBindings.exe'),
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/ui-screenshots')
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) {
    Write-Host 'UI screenshot capture skipped: Windows is required.'
    exit 0
}

if (-not (Test-Path $ExecutablePath -PathType Leaf)) {
    throw "Packaged executable not found: $ExecutablePath"
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AllyBindingsScreenshotNative {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
}
'@

function Wait-MainWindow([System.Diagnostics.Process]$Process, [int]$TimeoutSeconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Process.HasExited) { throw "Ally Bindings exited before its main window appeared (exit $($Process.ExitCode))." }
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) { return $Process.MainWindowHandle }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Timed out waiting for the Ally Bindings main window.'
}

function Find-ElementContaining {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [System.Windows.Automation.ControlType]$ControlType
    )
    $all = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $all) {
        if ($element.Current.ControlType -eq $ControlType -and $element.Current.Name -like "*$Name*") { return $element }
    }
    throw "Could not find $ControlType containing '$Name'."
}

function Select-Section {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$AutomationId)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $item = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (-not $item) { throw "Could not find app section $AutomationId." }
    $selection = [System.Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Start-Sleep -Milliseconds 450
    if (-not $selection.Current.IsSelected) { throw "App section $AutomationId did not become selected." }
}

function Save-WindowScreenshot {
    param([IntPtr]$Handle, [string]$Path)
    [AllyBindingsScreenshotNative+RECT]$rect = New-Object AllyBindingsScreenshotNative+RECT
    if (-not [AllyBindingsScreenshotNative]::GetWindowRect($Handle, [ref]$rect)) {
        throw 'GetWindowRect failed.'
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -lt 900 -or $height -lt 600) { throw "Unexpected screenshot dimensions: ${width}x${height}" }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $hdc = $graphics.GetHdc()
            try {
                if (-not [AllyBindingsScreenshotNative]::PrintWindow($Handle, $hdc, 2)) {
                    throw "PrintWindow failed with Win32 error $([Runtime.InteropServices.Marshal]::GetLastWin32Error())."
                }
            }
            finally { $graphics.ReleaseHdc($hdc) }
        }
        finally { $graphics.Dispose() }
        $colours = [System.Collections.Generic.HashSet[int]]::new()
        for ($x = 0; $x -lt $width; $x += 20) {
            for ($y = 0; $y -lt $height; $y += 20) { [void]$colours.Add($bitmap.GetPixel($x, $y).ToArgb()) }
        }
        if ($colours.Count -lt 24) { throw "Captured window has insufficient visual entropy ($($colours.Count) sampled colours)." }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
    if ((Get-Item $Path).Length -lt 25000) { throw "Screenshot looks empty or corrupt: $Path" }
}

$configRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'AllyBindings'
$configPath = Join-Path $configRoot 'config.json'
$backupPath = "$configPath.screenshot-backup"
$process = $null
try {
    if (Test-Path $backupPath) { Remove-Item $backupPath -Force }
    if (Test-Path $configPath) {
        New-Item -ItemType Directory -Path $configRoot -Force | Out-Null
        Move-Item $configPath $backupPath
    }
    else { New-Item -ItemType Directory -Path $configRoot -Force | Out-Null }

    $samplePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'examples/config.sample.json'
    $sample = (Get-Content $samplePath -Raw).Replace('"activeProfileId": "default"', '"activeProfileId": "elden-ring"')
    Set-Content -Path $configPath -Value $sample -Encoding utf8NoBOM

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    Get-ChildItem $OutputDirectory -Filter '*.png' -ErrorAction SilentlyContinue | Remove-Item -Force

    $process = Start-Process -FilePath $ExecutablePath -PassThru
    $handle = Wait-MainWindow -Process $process
    [AllyBindingsScreenshotNative]::ShowWindow($handle, 3) | Out-Null
    [AllyBindingsScreenshotNative]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Milliseconds 750
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)

    Select-Section -Root $root -AutomationId 'NavigationProfiles'
    Save-WindowScreenshot -Handle $handle -Path (Join-Path $OutputDirectory 'ally-bindings-profiles.png')
    Select-Section -Root $root -AutomationId 'NavigationController'
    Save-WindowScreenshot -Handle $handle -Path (Join-Path $OutputDirectory 'ally-bindings-controller.png')
    Select-Section -Root $root -AutomationId 'NavigationShortcut'
    Save-WindowScreenshot -Handle $handle -Path (Join-Path $OutputDirectory 'ally-bindings-shortcut.png')
    Select-Section -Root $root -AutomationId 'NavigationCaptureUpdate'
    Save-WindowScreenshot -Handle $handle -Path (Join-Path $OutputDirectory 'ally-bindings-capture-update.png')

    Write-Host "Captured Ally Bindings UI screenshots in $OutputDirectory"
}
finally {
    if ($process) {
        try { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null } } catch { }
        $process.Dispose()
    }
    if (Test-Path $configPath) { Remove-Item $configPath -Force }
    if (Test-Path $backupPath) { Move-Item $backupPath $configPath -Force }
}