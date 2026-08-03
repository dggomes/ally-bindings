$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$builder = Join-Path $repo 'lab/Build-HardwareValidator-Evidence.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) "ally-validator-evidence-test-$([Guid]::NewGuid().ToString('N'))"
$evidence = Join-Path $root 'input'
$output = Join-Path $root 'output'
$approvedSha = '0123456789abcdef0123456789abcdef01234567'
$sessionId = '01234567-89ab-cdef-0123-456789abcdef'
$logicalHash = 'fb0f2ac8167350edf147fb839be2306ccb15494c824a44badeff7aad083cf38b'
$wireHash = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Write-Audit([string]$Name, [string]$Outcome) {
    $audit = [ordered]@{
        SchemaVersion = 3
        ValidatorVersion = 'test'
        SessionId = $sessionId
        CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Model = 'RC73XA'
        VendorId = 0x0B05
        ProductId = 0x1B4C
        CompatibleInterfaceCount = 1
        FeatureReportLength = 50
        FixedMapping = 'M1=A; M2=B'
        LogicalPacketSha256 = $logicalHash
        WirePacketHex = '5A'
        WirePacketSha256 = $wireHash
        Outcome = $Outcome
        Detail = 'test'
        AttemptedInterfaces = 0
        SuccessfulInterfaces = 0
        RecoveryRequired = $true
        ArmouryRecoveryConfirmed = $false
    }
    Write-Utf8 (Join-Path $evidence $Name) (($audit | ConvertTo-Json) + "`n")
}

try {
    New-Item -ItemType Directory -Path $evidence, $output -Force | Out-Null
    Write-Utf8 (Join-Path $evidence 'RESULT.txt') "Approved main commit SHA: $approvedSha`n"
    Write-Utf8 (Join-Path $evidence 'attestation-verification.txt') "source digest: $approvedSha`n"
    Write-Audit 'pre-write-audit.json' 'pre-write'
    Write-Audit 'pending-set-feature-audit.json' 'pending-set-feature'
    Write-Audit 'outcome-audit.json' 'hid-api-accepted'
    foreach ($name in @(
        'baseline-armoury.png',
        'post-write-controller.png',
        'post-restore-armoury.png',
        'post-restore-controller.png'
    )) {
        [IO.File]::WriteAllBytes((Join-Path $evidence $name), [byte[]](1, 2, 3, 4))
    }

    $outputLines = @(& $builder -EvidenceDirectory $evidence -ApprovedSha $approvedSha -OutputDirectory $output)
    if ($outputLines -notcontains 'CONTROLLED HARDWARE EVIDENCE BUNDLE CREATED') { throw 'Evidence builder did not report success.' }
    $zip = @(Get-ChildItem -LiteralPath $output -Filter '*.zip' -File)
    $outer = @(Get-ChildItem -LiteralPath $output -Filter '*.sha256.txt' -File)
    if ($zip.Count -ne 1 -or $outer.Count -ne 1) { throw 'Evidence builder did not create exactly one bundle and outer hash.' }

    $expanded = Join-Path $root 'expanded'
    Expand-Archive -LiteralPath $zip[0].FullName -DestinationPath $expanded
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $expanded 'EVIDENCE-MANIFEST.json') | ConvertFrom-Json
    if ($manifest.approvedMainCommit -cne $approvedSha -or $manifest.files.Count -ne 9) {
        throw 'Evidence manifest did not bind the approved SHA and all nine source files.'
    }
    $actualOuter = (Get-FileHash -LiteralPath $zip[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-Content -Raw -LiteralPath $outer[0].FullName).IndexOf($actualOuter, [StringComparison]::Ordinal) -lt 0) {
        throw 'Outer hash sidecar does not match the evidence bundle.'
    }

    Remove-Item -LiteralPath $zip[0].FullName, $outer[0].FullName -Force
    Remove-Item -LiteralPath (Join-Path $evidence 'post-restore-controller.png')
    $rejected = $false
    try { & $builder -EvidenceDirectory $evidence -ApprovedSha $approvedSha -OutputDirectory $output | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Evidence builder accepted a missing physical proof file.' }

    Write-Output 'Hardware validator evidence sealing tests passed.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
