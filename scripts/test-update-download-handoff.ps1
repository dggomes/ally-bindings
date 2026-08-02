param(
    [string]$BuildRoot = 'src/AllyBindings.Windows/bin/Release/net8.0-windows10.0.19041.0/win-x64'
)

$ErrorActionPreference = 'Stop'
$BuildRoot = [IO.Path]::GetFullPath((Join-Path $PWD $BuildRoot))
$coreAssembly = Join-Path $BuildRoot 'AllyBindings.Core.dll'
$windowsAssembly = Join-Path $BuildRoot 'AllyBindings.dll'
if (-not (Test-Path -LiteralPath $coreAssembly -PathType Leaf) -or
    -not (Test-Path -LiteralPath $windowsAssembly -PathType Leaf)) {
    throw "Built Ally Bindings assemblies were not found under $BuildRoot."
}

Add-Type -Path $coreAssembly
Add-Type -Path $windowsAssembly
Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

public sealed class AllyBindingsStaticUpdateHandler : HttpMessageHandler
{
    private readonly byte[] payload;

    public AllyBindingsStaticUpdateHandler(byte[] payload) => this.payload = payload;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
            RequestMessage = request,
        };
        response.Content.Headers.ContentLength = payload.LongLength;
        return Task.FromResult(response);
    }
}
'@

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "ally-bindings-download-handoff-$([Guid]::NewGuid().ToString('N'))"
$payloadRoot = Join-Path $testRoot 'payload'
$archivePath = Join-Path $testRoot 'payload.zip'
$managedUpdatesRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'AllyBindings/updates'
$abandonedRoot = Join-Path $managedUpdatesRoot "integration-abandoned-$([Guid]::NewGuid().ToString('N'))"
$prepared = $null
$service = $null
try {
    New-Item -ItemType Directory -Force -Path $payloadRoot | Out-Null
    [IO.File]::WriteAllText((Join-Path $payloadRoot 'AllyBindings.exe'), 'verified updater handoff fixture')
    Compress-Archive -LiteralPath (Join-Path $payloadRoot 'AllyBindings.exe') -DestinationPath $archivePath
    $archiveBytes = [IO.File]::ReadAllBytes($archivePath)
    $sha256 = ([Security.Cryptography.SHA256]::HashData($archiveBytes) | ForEach-Object { $_.ToString('x2') }) -join ''

    $handler = [AllyBindingsStaticUpdateHandler]::new($archiveBytes)
    New-Item -ItemType Directory -Force -Path $abandonedRoot | Out-Null
    [IO.File]::WriteAllText((Join-Path $abandonedRoot 'update.zip'), 'orphaned failed download')
    [IO.Directory]::SetLastWriteTimeUtc($abandonedRoot, [DateTime]::UtcNow.AddMinutes(-5))
    $service = [AllyBindings.Windows.GitHubUpdateService]::new($handler)
    if (Test-Path -LiteralPath $abandonedRoot) {
        throw 'Updater startup did not remove an abandoned incomplete-download directory.'
    }
    $candidate = [AllyBindings.Core.UpdateCandidate]::new(
        [Version]'99.0.0',
        'v99.0.0-preview.1',
        'Updater handoff fixture',
        [Uri]'https://github.com/dggomes/ally-bindings/releases/tag/v99.0.0-preview.1',
        [Uri]'https://github.com/dggomes/ally-bindings/releases/download/v99.0.0-preview.1/AllyBindings-v99.0.0-preview.1-win-x64.zip',
        $sha256,
        $true)

    $prepared = $service.DownloadAndPrepareAsync($candidate, $null, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $stagedExe = Join-Path $prepared.PackageRoot 'AllyBindings.exe'
    if (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf)) {
        throw 'Download handoff did not extract the verified application executable.'
    }
    if ((Get-FileHash $stagedExe -Algorithm SHA256).Hash -ine $prepared.ExecutableSha256) {
        throw 'Download handoff did not bind the staged executable to its verified archive digest.'
    }

    $downloadedZip = Join-Path $prepared.UpdateRoot 'update.zip'
    $exclusive = [IO.FileStream]::new($downloadedZip, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $exclusive.Dispose()
    Remove-Item -LiteralPath $prepared.UpdateRoot -Recurse -Force
    if (Test-Path -LiteralPath $prepared.UpdateRoot) {
        throw 'Verified update staging remained locked after DownloadAndPrepareAsync returned.'
    }

    Write-Output 'Update download handoff passed: download handles closed before verification, extraction completed, and staging was exclusively reopenable and removable.'
}
finally {
    if ($null -ne $service) { $service.Dispose() }
    if ($null -ne $prepared) {
        Remove-Item -LiteralPath $prepared.UpdateRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $abandonedRoot -Recurse -Force -ErrorAction SilentlyContinue
}
