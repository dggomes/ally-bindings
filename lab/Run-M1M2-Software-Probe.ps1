[CmdletBinding()]
param(
    [string]$Session
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$probe = Join-Path $root 'AllyBindings.M1M2Probe.exe'

if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) {
    throw "Probe executable not found: $probe"
}

function Invoke-Probe {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    & $probe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Probe command failed with exit code $LASTEXITCODE."
    }
}

function Set-Checkpoint {
    param([string]$Name, [string]$Prompt)
    $answer = Read-Host "$Prompt [pass/fail/skipped/unknown]"
    if ($answer -notin @('pass', 'fail', 'skipped', 'unknown')) {
        Write-Warning 'Checkpoint not recorded: expected pass, fail, skipped or unknown.'
        return
    }
    $checkpointArguments = @('checkpoint', '--session', $script:Session, '--name', $Name, '--result', $answer)
    Invoke-Probe @checkpointArguments
}

function Assert-CheckpointPassed {
    param([string]$Name, [string]$RequiredStage)
    $evidence = Get-Content -LiteralPath (Join-Path $script:Session 'session.json') -Raw | ConvertFrom-Json
    $checkpoint = @($evidence.checkpoints | Where-Object { $_.name -eq $Name }) | Select-Object -Last 1
    if ($null -eq $checkpoint -or $checkpoint.result -ne 'pass') {
        throw "Complete and pass '$RequiredStage' before continuing."
    }
}

Write-Host 'ALLY BINDINGS M1/M2 SOFTWARE PROBE' -ForegroundColor Cyan
Write-Host 'No ASUS HID writes. No driver installation. No device hiding.' -ForegroundColor Yellow
Write-Host ''

Invoke-Probe inspect

if ([string]::IsNullOrWhiteSpace($Session)) {
    $startOutput = @(& $probe start)
    if ($LASTEXITCODE -ne 0 -or $startOutput.Count -lt 1) {
        throw 'Could not create an evidence session.'
    }
    $Session = [string]$startOutput[0]
}
if (-not (Test-Path -LiteralPath (Join-Path $Session 'session.json') -PathType Leaf)) {
    throw "Evidence session not found: $Session"
}
$sessionMetadata = Get-Content -LiteralPath (Join-Path $Session 'session.json') -Raw | ConvertFrom-Json
if ($sessionMetadata.schemaVersion -ne 2) {
    throw "Evidence session schema $($sessionMetadata.schemaVersion) uses the retired F17/F18 contract. Start this runner without -Session to create a fresh F11/F12 session."
}
$script:Session = $Session
$resumeFile = Join-Path $Session 'RESUME.txt'
$resumeLauncher = Join-Path $env:LOCALAPPDATA 'AllyBindings/Resume-M1M2-Software-Probe.ps1'
$resumeLauncherParent = Split-Path -Parent $resumeLauncher
New-Item -ItemType Directory -Path $resumeLauncherParent -Force | Out-Null
@(
    'Resume this exact evidence session after reboot with:'
    ".\Run-Software-Probe.ps1 -Session `"$Session`""
) | Set-Content -LiteralPath $resumeFile -Encoding UTF8
@(
    'Set-ExecutionPolicy -Scope Process Bypass -Force'
    "& '$((Join-Path $root 'Run-Software-Probe.ps1').Replace("'", "''"))' -Session '$($Session.Replace("'", "''"))'"
) | Set-Content -LiteralPath $resumeLauncher -Encoding UTF8
Write-Host "Session: $Session" -ForegroundColor Green
Write-Host "Cold-boot resume launcher: $resumeLauncher" -ForegroundColor Green

while ($true) {
    Write-Host ''
    Write-Host 'Choose the next controlled stage:'
    Write-Host '  1  Baseline screenshots recorded'
    Write-Host '  2  Assign M1=F12 and M2=F11 through Armoury'
    Write-Host '  3  Capture F11/F12 events'
    Write-Host '  4  Remote Play virtual-only bridge test'
    Write-Host '  5  Remote Play coexistence bridge test'
    Write-Host '  6  Record HidHide requirement'
    Write-Host '  7  Cold-boot persistence capture'
    Write-Host '  8  Confirm Armoury restoration'
    Write-Host '  9  Finalize evidence ZIP'
    Write-Host '  Q  Quit without finalizing'
    $choice = (Read-Host 'Selection').Trim().ToUpperInvariant()

    switch ($choice) {
        '1' {
            Set-Checkpoint 'armoury-baseline-saved' 'Are all four M1/M2 assignments and component versions saved?'
        }
        '2' {
            Assert-CheckpointPassed 'armoury-baseline-saved' 'stage 1: baseline screenshots'
            Write-Host 'Clear both secondary assignments in Armoury first.' -ForegroundColor Yellow
            Write-Host "In Armoury's virtual keyboard, click F12 for M1 primary and F11 for M2 primary." -ForegroundColor Yellow
            Read-Host 'Press Enter only after Armoury visibly shows both assignments'
            Set-Checkpoint 'f11-f12-assigned' 'Does Armoury show M1=F12, M2=F11, with both secondaries empty?'
        }
        '3' {
            Assert-CheckpointPassed 'f11-f12-assigned' 'stage 2: F11/F12 assignment'
            Write-Host 'Press/release M1 twice, M2 twice, then hold each once during the capture.' -ForegroundColor Yellow
            Invoke-Probe listen --session $Session --seconds 30
            Write-Host 'Now open Notepad. During the suppression pass, press M1/M2 and type probe using unrelated keys.' -ForegroundColor Yellow
            Write-Host 'F11/F12 must be swallowed while the unrelated text still appears.' -ForegroundColor Yellow
            Read-Host 'Press Enter when Notepad is focused and you are ready'
            Invoke-Probe listen --session $Session --seconds 15 --suppress
            Set-Checkpoint 'keyboard-capture' 'Were clean F12/F11 edges observed, then suppressed while unrelated keys still worked?'
        }
        '4' {
            Assert-CheckpointPassed 'keyboard-capture' 'stage 3: capture and suppression proof'
            Write-Host 'Open Remote Play first and navigate to a screen where A and B have obvious effects.' -ForegroundColor Yellow
            Write-Host 'Confirm touch or an external keyboard can reopen ASUS Command Center.' -ForegroundColor Yellow
            Write-Host 'After the bridge reports connected, disable Embedded Controller during the timed window.' -ForegroundColor Yellow
            Read-Host 'Press Enter when Remote Play and the recovery input are ready'
            try {
                Invoke-Probe bridge --session $Session --seconds 120
            }
            finally {
                Write-Host 'Re-enable Embedded Controller through ASUS Command Center now.' -ForegroundColor Red
                Read-Host 'Press Enter after Embedded Controller is re-enabled'
            }
            Set-Checkpoint 'remote-play-virtual-only' 'Did Remote Play accept M1->A and M2->B from the virtual controller?'
        }
        '5' {
            Assert-CheckpointPassed 'remote-play-virtual-only' 'stage 4: virtual-only Remote Play'
            Write-Host 'Keep Embedded Controller enabled. Check for duplicates, wrong slot, or ignored virtual input.' -ForegroundColor Yellow
            Read-Host 'Press Enter when Remote Play is ready'
            Invoke-Probe bridge --session $Session --seconds 120
            Set-Checkpoint 'remote-play-coexistence' 'Did physical and virtual controllers coexist without duplicate/slot problems?'
        }
        '6' {
            $evidence = Get-Content -LiteralPath (Join-Path $Session 'session.json') -Raw | ConvertFrom-Json
            if (-not @($evidence.checkpoints | Where-Object { $_.name -eq 'remote-play-coexistence' }).Count) {
                throw 'Record stage 5 coexistence before deciding whether HidHide is required.'
            }
            Set-Checkpoint 'hidhide-required' 'Is a separate HidHide test required? Use pass=yes, fail=no, skipped=not assessed'
        }
        '7' {
            Assert-CheckpointPassed 'keyboard-capture' 'stage 3: capture and suppression proof'
            $shutdownDone = (Read-Host 'Has a full shutdown already completed with Fast Startup and remapper startup disabled? [y/N]').Trim()
            if ($shutdownDone -notmatch '^[Yy]$') {
                Write-Host '1. Disable Fast Startup temporarily.' -ForegroundColor Yellow
                Write-Host '2. Disable remapper startup, but leave Armoury F11/F12 assignments intact.' -ForegroundColor Yellow
                Write-Host '3. Run: shutdown /s /t 0' -ForegroundColor Yellow
                Write-Host "4. After boot, run $resumeLauncher and choose stage 7 again." -ForegroundColor Yellow
                return
            }
            Invoke-Probe listen --session $Session --seconds 30
            Set-Checkpoint 'cold-boot-persistence' 'Did the F12/F11 mapping survive the cold boot?'
        }
        '8' {
            Set-Checkpoint 'armoury-restored' 'Are all original Armoury assignments restored and physically verified?'
        }
        '9' {
            Invoke-Probe finalize --session $Session
            Write-Host 'Evidence finalized. Keep the displayed SHA-256 separately.' -ForegroundColor Green
            return
        }
        'Q' { return }
        default { Write-Warning 'Unknown selection.' }
    }
}
