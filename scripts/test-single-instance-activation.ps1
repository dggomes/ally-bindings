param(
    [string]$ExecutablePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/AllyBindings-win-x64/AllyBindings.exe')
)

$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    Write-Output 'Single-instance activation integration test skipped: Windows is required.'
    exit 0
}
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw "Packaged executable is missing: $ExecutablePath"
}

$first = $null
$second = $null
try {
    $first = Start-Process -FilePath $ExecutablePath -ArgumentList '--background' -PassThru
    Start-Sleep -Seconds 3
    $first.Refresh()
    if ($first.HasExited) {
        throw "The background startup instance exited unexpectedly with code $($first.ExitCode)."
    }
    if ($first.MainWindowHandle -ne [IntPtr]::Zero) {
        throw 'The --background startup instance opened a visible main window.'
    }

    $second = Start-Process -FilePath $ExecutablePath -PassThru
    if (-not $second.WaitForExit(10000)) {
        throw 'The second launch did not hand off activation and exit promptly.'
    }
    if ($second.ExitCode -ne 0) {
        throw "The activation handoff process exited with code $($second.ExitCode)."
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 200
        $first.Refresh()
    } while ($first.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)

    if ($first.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'Launching Ally Bindings again did not reveal the existing tray instance.'
    }
}
finally {
    if ($second) {
        if (-not $second.HasExited) { $second.Kill($true); $second.WaitForExit() }
        $second.Dispose()
    }
    if ($first) {
        if (-not $first.HasExited) { $first.Kill($true); $first.WaitForExit() }
        $first.Dispose()
    }
}

Write-Output 'Startup-background and second-instance window activation tests passed.'
