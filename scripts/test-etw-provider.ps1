$ErrorActionPreference = 'Stop'

if (-not $IsWindows -and $env:OS -ne 'Windows_NT') {
    throw 'This provider probe must run on Windows.'
}

$required = @(
    @{ Name = 'Microsoft-Windows-USB-UCX'; Guid = '36da592d-e43a-4e28-af6f-4bc57c5a11e8' },
    @{ Name = 'Microsoft-Windows-USB-USBXHCI'; Guid = '30e1d284-5d88-459c-83fd-6345b39b19ec' },
    @{ Name = 'Microsoft-Windows-USB-USBHUB3'; Guid = 'ac52ad17-cc01-4f85-8df5-4dce4333c99b' }
)

$inventory = & logman query providers 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "logman could not enumerate Windows ETW providers: $inventory"
}

foreach ($provider in $required) {
    if ($inventory.IndexOf($provider.Name, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Windows does not expose required USB ETW provider $($provider.Name)."
    }

    $detail = & logman query providers $provider.Name 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "logman could not inspect $($provider.Name): $detail"
    }
    if ($detail.IndexOf($provider.Guid, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "$($provider.Name) did not report expected provider GUID $($provider.Guid)."
    }
    if ($detail.IndexOf('FullDataBusTrace', [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $detail -notmatch '(?is)FullDataBusTrace.{0,200}(?:0x0*100|0x0000000000000100)') {
        throw "$($provider.Name) does not publish the required FullDataBusTrace 0x100 keyword."
    }
}

Write-Output "Built-in USB ETW provider and FullDataBusTrace probe passed for: $($required.Name -join ', ')"
