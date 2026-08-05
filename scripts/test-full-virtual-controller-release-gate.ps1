$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$gate = Join-Path $root 'scripts/assert-full-virtual-controller-release-approved.ps1'
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) "ally-bindings-release-approval-$([Guid]::NewGuid().ToString('N')).json"
$head = (& git -C $root rev-parse HEAD).Trim()

try {
    $missingBlocked = $false
    try { & $gate -RepositoryRoot $root -EvidencePath $temporary -ReleaseCommit $head | Out-Null }
    catch { $missingBlocked = $_.Exception.Message -match 'evidence is missing' }
    if (-not $missingBlocked) { throw 'Missing physical approval evidence did not block release.' }

    $evidence = [ordered]@{
        schemaVersion = 1
        approved = $true
        testedCommit = $head
        testedAtUtc = '2026-08-05T09:00:00Z'
        tester = 'CI contract test'
        deviceModel = 'ROG Xbox Ally X'
        windowsVersion = 'contract-test'
        armouryVersion = 'contract-test'
        firmwareVersion = 'contract-test'
        edgeVersion = 'contract-test'
        evidenceBundleSha256 = ('a' * 64)
        verdicts = [ordered]@{
            fullMirror = 'PASS'
            paddleOverlay = 'PASS'
            controllerOnlyRecovery = 'PASS'
            edgeSingleControllerCoexistence = 'PASS'
            commandCentreArmouryCompatibility = 'PASS'
            safeWithoutHidHide = 'YES'
        }
    }
    $evidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporary -Encoding utf8
    & $gate -RepositoryRoot $root -EvidencePath $temporary -ReleaseCommit $head | Out-Null

    $evidence.verdicts.safeWithoutHidHide = 'NO'
    $evidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporary -Encoding utf8
    $unsafeBlocked = $false
    try { & $gate -RepositoryRoot $root -EvidencePath $temporary -ReleaseCommit $head | Out-Null }
    catch { $unsafeBlocked = $_.Exception.Message -match 'safeWithoutHidHide' }
    if (-not $unsafeBlocked) { throw 'Unsafe physical/virtual coexistence evidence did not block release.' }
}
finally {
    Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
}

Write-Output 'Physical full-controller release approval gate contract passed.'
