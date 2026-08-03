param(
    [string]$ExecutablePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/AllyBindings-HardwareValidator-win-x64/AllyBindings.HardwareValidator.exe')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw "Hardware validator executable is missing: $ExecutablePath"
}

$mtCommand = Get-Command mt.exe -ErrorAction SilentlyContinue
$mt = if ($mtCommand) { $mtCommand.Source } else {
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    Get-ChildItem -LiteralPath $kitsRoot -Filter mt.exe -File -Recurse |
        Where-Object { $_.FullName -match '[\\/]x64[\\/]mt\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $mt) { throw 'Windows SDK mt.exe was not found.' }

$tempManifest = Join-Path $env:TEMP ("ally-validator-manifest-" + [Guid]::NewGuid().ToString('N') + '.xml')
try {
    & $mt "-inputresource:$ExecutablePath;#1" "-out:$tempManifest"
    if ($LASTEXITCODE -ne 0) { throw "mt.exe failed with exit code $LASTEXITCODE." }
    [xml]$embedded = Get-Content -Raw -LiteralPath $tempManifest
    $nodes = @($embedded.SelectNodes("/*[local-name()='assembly']/*[local-name()='trustInfo']/*[local-name()='security']/*[local-name()='requestedPrivileges']/*[local-name()='requestedExecutionLevel']"))
    if ($nodes.Count -ne 1 -or
        $nodes[0].GetAttribute('level') -cne 'requireAdministrator' -or
        $nodes[0].GetAttribute('uiAccess') -cne 'false') {
        throw 'Published PE must embed exactly one requireAdministrator requestedExecutionLevel with uiAccess=false.'
    }
}
finally {
    if (Test-Path -LiteralPath $tempManifest) { Remove-Item -LiteralPath $tempManifest -Force }
}

Write-Output 'Published validator PE embeds exactly one requireAdministrator/uiAccess=false execution level.'
