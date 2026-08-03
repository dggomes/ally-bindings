param(
    [Parameter(Mandatory = $true)]
    [string]$EvidenceDirectory,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ApprovedSha,
    [string]$OutputDirectory = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$evidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $evidenceRoot -PathType Container)) {
    throw "Evidence directory is missing: $evidenceRoot"
}
if ($outputRoot.StartsWith($evidenceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be outside EvidenceDirectory.'
}

$exactNames = @(
    'RESULT.txt',
    'attestation-verification.txt',
    'pre-write-audit.json',
    'pending-set-feature-audit.json',
    'outcome-audit.json'
)
$mediaPrefixes = @(
    'baseline-armoury',
    'post-write-controller',
    'post-restore-armoury',
    'post-restore-controller'
)
$allowedMediaExtensions = @('.png', '.jpg', '.jpeg', '.mp4', '.mov')
$files = @(Get-ChildItem -LiteralPath $evidenceRoot -File)
if (@(Get-ChildItem -LiteralPath $evidenceRoot -Directory).Count -ne 0) {
    throw 'EvidenceDirectory must contain files only; subdirectories are forbidden.'
}
if ($files.Count -ne 9) { throw "Expected exactly 9 evidence files; found $($files.Count)." }
foreach ($file in $files) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Links are forbidden: $($file.Name)" }
    if ($file.Length -eq 0) { throw "Evidence file is empty: $($file.Name)" }
}
foreach ($name in $exactNames) {
    if (@($files | Where-Object Name -CEQ $name).Count -ne 1) { throw "Missing exact evidence file: $name" }
}
foreach ($prefix in $mediaPrefixes) {
    $matches = @($files | Where-Object {
        $_.BaseName -ceq $prefix -and $allowedMediaExtensions -ccontains $_.Extension.ToLowerInvariant()
    })
    if ($matches.Count -ne 1) { throw "Expected exactly one supported media file named $prefix.<extension>." }
}
$allowedNames = @($exactNames) + @($files | Where-Object { $mediaPrefixes -ccontains $_.BaseName } | Select-Object -ExpandProperty Name)
$extras = @($files | Where-Object { $allowedNames -cnotcontains $_.Name })
if ($extras.Count -ne 0) { throw "Unexpected evidence file: $($extras[0].Name)" }

$approvedShaLower = $ApprovedSha.ToLowerInvariant()
$resultText = Get-Content -Raw -LiteralPath (Join-Path $evidenceRoot 'RESULT.txt')
$attestationText = Get-Content -Raw -LiteralPath (Join-Path $evidenceRoot 'attestation-verification.txt')
foreach ($pair in @(
    @{ Name = 'RESULT.txt'; Text = $resultText },
    @{ Name = 'attestation-verification.txt'; Text = $attestationText }
)) {
    if ($pair.Text.IndexOf($approvedShaLower, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "$($pair.Name) does not contain the approved SHA."
    }
}

$auditNames = @('pre-write-audit.json', 'pending-set-feature-audit.json', 'outcome-audit.json')
$audits = @{}
foreach ($name in $auditNames) {
    $audits[$name] = Get-Content -Raw -LiteralPath (Join-Path $evidenceRoot $name) | ConvertFrom-Json
    if ($audits[$name].SchemaVersion -ne 3) { throw "$name has the wrong schema version." }
    if ($audits[$name].LogicalPacketSha256 -cne 'fb0f2ac8167350edf147fb839be2306ccb15494c824a44badeff7aad083cf38b') {
        throw "$name has the wrong logical packet hash."
    }
}
if ($audits['pre-write-audit.json'].Outcome -cne 'pre-write') { throw 'Pre-write audit has the wrong outcome.' }
if ($audits['pending-set-feature-audit.json'].Outcome -cne 'pending-set-feature') { throw 'Pending audit has the wrong outcome.' }
if ($audits['outcome-audit.json'].Outcome -in @('pre-write', 'pending-set-feature')) { throw 'Outcome audit is not terminal.' }
$sessionIds = @($auditNames | ForEach-Object { $audits[$_].SessionId } | Select-Object -Unique)
$wireHashes = @($auditNames | ForEach-Object { $audits[$_].WirePacketSha256 } | Select-Object -Unique)
if ($sessionIds.Count -ne 1 -or $wireHashes.Count -ne 1) { throw 'Audit session IDs or wire hashes do not agree.' }

[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$sessionId = [string]$sessionIds[0]
if ($sessionId -notmatch '^[0-9a-fA-F-]{16,64}$') { throw 'Audit session ID is not safe for an evidence filename.' }
$staging = Join-Path ([IO.Path]::GetTempPath()) "ally-validator-evidence-$([Guid]::NewGuid().ToString('N'))"
$zipPath = Join-Path $outputRoot "HardwareValidator-Evidence-$sessionId.zip"
$hashPath = "$zipPath.sha256.txt"
if ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $hashPath)) { throw 'Evidence output already exists.' }
try {
    [IO.Directory]::CreateDirectory($staging) | Out-Null
    foreach ($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $staging $file.Name) }
    $entries = @(Get-ChildItem -LiteralPath $staging -File | Sort-Object Name | ForEach-Object {
        [ordered]@{
            name = $_.Name
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    $manifest = [ordered]@{
        schemaVersion = 1
        approvedMainCommit = $approvedShaLower
        sessionId = $sessionId
        wirePacketSha256 = [string]$wireHashes[0]
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        files = $entries
    }
    [IO.File]::WriteAllText(
        (Join-Path $staging 'EVIDENCE-MANIFEST.json'),
        (($manifest | ConvertTo-Json -Depth 5) + "`n"),
        [Text.UTF8Encoding]::new($false))
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $outerHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($hashPath, "$outerHash  $([IO.Path]::GetFileName($zipPath))`n", [Text.UTF8Encoding]::new($false))
    Write-Output 'CONTROLLED HARDWARE EVIDENCE BUNDLE CREATED'
    Write-Output "Bundle: $zipPath"
    Write-Output "Outer SHA-256: $outerHash"
    Write-Output 'Send only the outer SHA-256 into the development thread before the bundle is reviewed.'
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
