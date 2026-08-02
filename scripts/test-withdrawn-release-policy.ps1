$ErrorActionPreference = 'Stop'
$assertScript = Join-Path $PSScriptRoot 'assert-release-tag-allowed.ps1'
$denylist = Join-Path (Split-Path -Parent $PSScriptRoot) '.github/withdrawn-release-tags.txt'

foreach ($tag in @('v0.3.0-preview.5', 'v0.3.0-preview.7')) {
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

& $assertScript -Tag 'v0.3.0-preview.10' -DenylistPath $denylist
Write-Output 'Withdrawn-release policy passed: preview.5 and preview.7 rejected; preview.10 accepted.'
