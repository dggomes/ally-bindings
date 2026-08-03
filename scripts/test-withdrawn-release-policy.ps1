$ErrorActionPreference = 'Stop'
$assertScript = Join-Path $PSScriptRoot 'assert-release-tag-allowed.ps1'
$denylist = Join-Path (Split-Path -Parent $PSScriptRoot) '.github/withdrawn-release-tags.txt'
$releaseWorkflow = Get-Content -Raw -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) '.github/workflows/release.yml')

if ($releaseWorkflow -notmatch 'git show origin/main:\.github/withdrawn-release-tags\.txt' -or
    $releaseWorkflow -notmatch 'DenylistPath \$authoritativeDenylist') {
    throw 'Release automation does not enforce the authoritative current-branch withdrawal policy.'
}

foreach ($tag in @('v0.3.0-preview.5', 'v0.3.0-preview.7', 'v0.3.0-preview.11', 'v0.3.0-preview.17', 'v0.3.0-preview.18')) {
    $withdrawnRejected = $false
    try {
        & $assertScript -Tag $tag -DenylistPath $denylist
    }
    catch {
        if ($_.Exception.Message -notlike '*permanently withdrawn*') { throw }
        $withdrawnRejected = $true
    }
    if (-not $withdrawnRejected) {
        throw "The withdrawn $tag tag was accepted by the release guard."
    }
}

& $assertScript -Tag 'v0.3.0-preview.19' -DenylistPath $denylist
Write-Output 'Withdrawn-release policy passed: preview.5, preview.7, preview.11, preview.17, and preview.18 rejected; preview.19 accepted.'
