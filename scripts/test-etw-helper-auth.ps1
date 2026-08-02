param(
    [string]$ExecutablePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/AllyBindings-win-x64/AllyBindings.exe')
)

$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    Write-Output 'ETW helper authentication integration test skipped: Windows is required.'
    exit 0
}
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw "Packaged executable is missing: $ExecutablePath"
}

function Invoke-RejectedHelperPeer {
    param(
        [int]$ClaimedParentProcessId,
        [string]$ExpectedErrorFragment
    )

    $sessionId = [Guid]::NewGuid()
    $pipeName = "AllyBindings.ArmouryEtw.$($sessionId.ToString('D'))"
    $options = [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly
    $server = [IO.Pipes.NamedPipeServerStream]::new(
        $pipeName,
        [IO.Pipes.PipeDirection]::InOut,
        1,
        [IO.Pipes.PipeTransmissionMode]::Byte,
        $options)
    $process = $null
    try {
        $connect = $server.WaitForConnectionAsync()
        $process = Start-Process -FilePath $ExecutablePath -ArgumentList @(
            '--armoury-etw-capture-helper',
            $sessionId.ToString('D'),
            $ClaimedParentProcessId.ToString([Globalization.CultureInfo]::InvariantCulture)
        ) -PassThru
        if (-not $connect.Wait([TimeSpan]::FromSeconds(15))) {
            throw 'The helper did not connect to the adversarial pipe peer.'
        }

        $reader = [IO.StreamReader]::new($server, [Text.Encoding]::UTF8, $false, 1024, $true)
        try {
            $responseTask = $reader.ReadLineAsync()
            if (-not $responseTask.Wait([TimeSpan]::FromSeconds(15))) {
                throw 'The helper did not return an authentication rejection.'
            }
            $response = $responseTask.Result
        }
        finally {
            $reader.Dispose()
        }

        if (-not $process.WaitForExit(15000)) {
            $process.Kill($true)
            throw 'The rejected helper did not exit promptly.'
        }
        if ($process.ExitCode -ne 1) {
            throw "The adversarial helper peer was not rejected (exit code $($process.ExitCode))."
        }
        if ($response.IndexOf($ExpectedErrorFragment, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Unexpected helper rejection: $response"
        }
        $diagnosticPath = Join-Path $env:LOCALAPPDATA "AllyBindings/diagnostics/armoury-etw-$($sessionId.ToString('D')).json"
        if (-not (Test-Path -LiteralPath $diagnosticPath -PathType Leaf)) {
            throw 'The rejected helper did not persist an in-app diagnostic.'
        }
        $diagnostic = Get-Content -Raw -LiteralPath $diagnosticPath | ConvertFrom-Json
        if ($diagnostic.SchemaVersion -ne 1 -or $diagnostic.SessionId -ne $sessionId.ToString('D') -or
            $diagnostic.Stage -ne 'helper-failed' -or
            $diagnostic.ErrorMessage.IndexOf($ExpectedErrorFragment, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "The helper diagnostic did not preserve the authenticated failure: $($diagnostic | ConvertTo-Json -Compress)"
        }
        if ($diagnostic.Privacy.IndexOf('No USB payloads', [StringComparison]::Ordinal) -lt 0) {
            throw 'The helper diagnostic does not declare its payload privacy boundary.'
        }
        Remove-Item -LiteralPath $diagnosticPath -Force
    }
    finally {
        if ($process) {
            if (-not $process.HasExited) {
                $process.Kill($true)
                $process.WaitForExit()
            }
            $process.Dispose()
        }
        $server.Dispose()
    }
}

# A forged parent PID must fail before any ETW session is created.
Invoke-RejectedHelperPeer `
    -ClaimedParentProcessId ([Math]::Min([int]::MaxValue, $PID + 1000000)) `
    -ExpectedErrorFragment 'expected unelevated Ally Bindings parent process'

# Even the real pipe-server PID is insufficient when its executable is PowerShell,
# rather than the exact Ally Bindings executable acting as the elevated helper.
Invoke-RejectedHelperPeer `
    -ClaimedParentProcessId $PID `
    -ExpectedErrorFragment 'not the same Ally Bindings executable'

# Exercise the Windows sharing semantics relied on by the process-lifetime image lock.
$integrityLock = [IO.File]::Open(
    $ExecutablePath,
    [IO.FileMode]::Open,
    [IO.FileAccess]::Read,
    [IO.FileShare]::Read)
try {
    $replacementRejected = $false
    try {
        $writer = [IO.File]::Open(
            $ExecutablePath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        $writer.Dispose()
    }
    catch [IO.IOException] {
        $replacementRejected = $true
    }
    if (-not $replacementRejected) {
        throw 'Windows allowed executable replacement while the integrity lock was held.'
    }
}
finally {
    $integrityLock.Dispose()
}

Write-Output 'ETW helper PID/path authentication and executable-lock integration tests passed.'
