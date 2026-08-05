param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$EvidencePath = 'docs/evidence/full-virtual-controller-release-approval.json',
    [string]$ReleaseCommit = $env:GITHUB_SHA
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
if (-not [System.IO.Path]::IsPathRooted($EvidencePath)) {
    $EvidencePath = Join-Path $RepositoryRoot $EvidencePath
}
if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) {
    throw "Release blocked: physical full-controller approval evidence is missing at $EvidencePath. Complete docs/FULL-VIRTUAL-CONTROLLER-VALIDATION.md first."
}

$evidence = Get-Content -Raw -LiteralPath $EvidencePath | ConvertFrom-Json
if ($evidence.schemaVersion -ne 1 -or $evidence.approved -ne $true) {
    throw 'Release blocked: approval evidence must use schemaVersion 1 and approved=true.'
}

$requiredVerdicts = @{
    fullMirror = 'PASS'
    paddleOverlay = 'PASS'
    controllerOnlyRecovery = 'PASS'
    edgeSingleControllerCoexistence = 'PASS'
    commandCentreArmouryCompatibility = 'PASS'
    safeWithoutHidHide = 'YES'
}
foreach ($entry in $requiredVerdicts.GetEnumerator()) {
    $actual = [string]$evidence.verdicts.($entry.Key)
    if ($actual -cne $entry.Value) {
        throw "Release blocked: verdict '$($entry.Key)' must be '$($entry.Value)', got '$actual'."
    }
}

$testedCommit = [string]$evidence.testedCommit
if ($testedCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'Release blocked: testedCommit must be a full lowercase Git commit SHA.'
}
if ([string]::IsNullOrWhiteSpace($ReleaseCommit)) {
    $ReleaseCommit = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
}
if ($ReleaseCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'Release blocked: release commit must be a full lowercase Git commit SHA.'
}
& git -C $RepositoryRoot cat-file -e "$testedCommit^{commit}"
if ($LASTEXITCODE -ne 0) { throw "Release blocked: tested commit $testedCommit is not present in the repository." }
& git -C $RepositoryRoot merge-base --is-ancestor $testedCommit $ReleaseCommit
if ($LASTEXITCODE -ne 0) {
    throw "Release blocked: physically tested commit $testedCommit is not an ancestor of release commit $ReleaseCommit."
}

foreach ($field in @('testedAtUtc', 'tester', 'deviceModel', 'windowsVersion', 'armouryVersion', 'firmwareVersion', 'edgeVersion')) {
    if ([string]::IsNullOrWhiteSpace([string]$evidence.$field)) {
        throw "Release blocked: evidence field '$field' is required."
    }
}
$testedAt = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse([string]$evidence.testedAtUtc, [ref]$testedAt)) {
    throw 'Release blocked: testedAtUtc is not a valid timestamp.'
}
if ([string]$evidence.evidenceBundleSha256 -notmatch '^[0-9a-f]{64}$') {
    throw 'Release blocked: evidenceBundleSha256 must be a lowercase SHA-256 digest.'
}

Write-Output "Physical full-controller release approval accepted for tested commit $testedCommit."
