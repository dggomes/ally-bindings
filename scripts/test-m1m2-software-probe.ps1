param(
    [string]$PackageRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/AllyBindings-M1M2-SoftwareProbe-win-x64')
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PackageRoot 'AllyBindings.M1M2Probe.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing packaged probe: $exe" }

function Invoke-ExpectedSuccess {
    param([string[]]$Arguments)
    $output = @(& $exe @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Expected success for '$($Arguments -join ' ')'; exit=$LASTEXITCODE; output=$($output -join ' | ')"
    }
    return $output
}

$help = Invoke-ExpectedSuccess @('help')
if (($help -join "`n") -notmatch 'no ASUS HID writes' -or ($help -join "`n") -notmatch 'bridge') {
    throw 'Help output is missing the safety boundary or bridge command.'
}

$unsupported = @(& $exe write-m1-a-m2-b 2>&1)
if ($LASTEXITCODE -ne 64 -or ($unsupported -join "`n") -notmatch 'Unknown command') {
    throw 'The retired hardware-write command was not rejected as a usage error.'
}

$inspect = Invoke-ExpectedSuccess @('inspect')
if (($inspect -join "`n") -notmatch 'READ-ONLY') {
    throw 'Inspect did not print its read-only result.'
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "ally-bindings-software-probe-ci-$([Guid]::NewGuid().ToString('N'))"
try {
    $start = Invoke-ExpectedSuccess @('start', '--root', $tempRoot)
    $session = [string]$start[0]
    if (-not (Test-Path -LiteralPath (Join-Path $session 'session.json') -PathType Leaf)) {
        throw 'Start did not create session.json.'
    }

    $checkpoints = @(
        'armoury-baseline-saved',
        'f17-f18-assigned',
        'keyboard-capture',
        'remote-play-virtual-only',
        'remote-play-coexistence',
        'hidhide-required',
        'cold-boot-persistence',
        'armoury-restored'
    )
    foreach ($checkpoint in $checkpoints) {
        $result = if ($checkpoint -in @('armoury-baseline-saved', 'armoury-restored')) { 'pass' } else { 'skipped' }
        Invoke-ExpectedSuccess @(
            'checkpoint', '--session', $session,
            '--name', $checkpoint, '--result', $result
        ) | Out-Null
    }

    $zipPath = Join-Path $tempRoot 'evidence.zip'
    $finalize = Invoke-ExpectedSuccess @('finalize', '--session', $session, '--out', $zipPath)
    if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf) -or ($finalize -join "`n") -notmatch 'SHA-256: [0-9a-f]{64}') {
        throw 'Finalize did not create and hash the evidence ZIP.'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $names = @($archive.Entries | ForEach-Object FullName | Sort-Object)
        if (@(Compare-Object @('manifest.json', 'README.txt', 'session.json') $names).Count -ne 0) {
            throw "Unexpected evidence ZIP entries: $($names -join ', ')"
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}

Write-Output 'WINDOWS SOFTWARE PROBE SMOKE PASSED'
