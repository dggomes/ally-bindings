$ErrorActionPreference = 'Stop'
$assertScript = Join-Path $PSScriptRoot 'assert-release-tag-allowed.ps1'
$denylist = Join-Path (Split-Path -Parent $PSScriptRoot) '.github/withdrawn-release-tags.txt'

$withdrawnRejected = $false
try {
    & $assertScript -Tag 'v0.3.0-preview.5' -DenylistPath $denylist
} catch {
    if ($_.Exception.Message -notlike '*permanently withdrawn*') { throw }
    $withdrawnRejected = $true
}
if (-not $withdrawnRejected) {
    throw 'The withdrawn preview.5 tag was accepted by the release guard.'
}

& $assertScript -Tag 'v0.3.0-preview.6' -DenylistPath $denylist
Write-Output 'Withdrawn-release policy passed: preview.5 rejected and preview.6 accepted.'
