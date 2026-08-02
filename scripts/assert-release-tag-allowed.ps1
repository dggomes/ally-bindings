param(
    [Parameter(Mandatory=$true)][string]$Tag,
    [string]$DenylistPath = '.github/withdrawn-release-tags.txt'
)

$ErrorActionPreference = 'Stop'
$withdrawn = @(Get-Content -LiteralPath $DenylistPath |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -and -not $_.StartsWith('#') })
if ($withdrawn -ccontains $Tag) {
    throw "Release tag $Tag is permanently withdrawn and cannot publish assets. Use a new version."
}
Write-Output "Release tag $Tag is not withdrawn."
