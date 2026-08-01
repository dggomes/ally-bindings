param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [string]$ChangelogPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ChangelogPath)) {
    throw "Changelog not found: $ChangelogPath"
}

$lines = Get-Content -LiteralPath $ChangelogPath
$heading = "## [$Tag]"
$releaseHeading = $lines | Where-Object { $_.StartsWith($heading, [StringComparison]::Ordinal) } | Select-Object -First 1
$start = [Array]::IndexOf($lines, $releaseHeading)
if ($start -lt 0) {
    throw "CHANGELOG.md has no release section beginning with '$heading'."
}

$end = $lines.Count
for ($index = $start + 1; $index -lt $lines.Count; $index++) {
    if ($lines[$index] -match '^## \[') {
        $end = $index
        break
    }
}

$notes = @($lines[($start + 1)..($end - 1)] | Where-Object { $_ -notmatch '^\[[^]]+\]:\s+' })
while ($notes.Count -gt 0 -and [string]::IsNullOrWhiteSpace($notes[0])) {
    $notes = $notes[1..($notes.Count - 1)]
}
while ($notes.Count -gt 0 -and [string]::IsNullOrWhiteSpace($notes[-1])) {
    $notes = $notes[0..($notes.Count - 2)]
}
if ($notes.Count -eq 0) {
    throw "The changelog section for $Tag is empty."
}

$parent = Split-Path -Parent $OutputPath
if ($parent) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}
Set-Content -LiteralPath $OutputPath -Value $notes -Encoding utf8NoBOM
Write-Output "Extracted release notes for $Tag to $OutputPath"
