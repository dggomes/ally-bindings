$ErrorActionPreference = 'Stop'

if (-not $IsWindows -and $env:OS -ne 'Windows_NT') {
    Write-Host 'Armoury tap runtime test skipped: Windows is required.'
    exit 0
}

$repo = Split-Path -Parent $PSScriptRoot
$nativeDll = Join-Path $repo 'native/ArmouryTap/build/Release/AllyBindings.ArmouryTap.dll'
$managedDll = Join-Path $repo 'src/AllyBindings.Windows/bin/Release/net8.0-windows10.0.19041.0/win-x64/AllyBindings.dll'
$coreDll = Join-Path $repo 'src/AllyBindings.Core/bin/Release/net8.0/AllyBindings.Core.dll'
if (-not (Test-Path $nativeDll -PathType Leaf)) { throw "Native tap DLL not found: $nativeDll" }
if (-not (Test-Path $managedDll -PathType Leaf)) { throw "Managed Windows assembly not found: $managedDll" }
if (-not (Test-Path $coreDll -PathType Leaf)) { throw "Managed core assembly not found: $coreDll" }

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ArmouryTapRuntimeNative
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeLibrary(IntPtr module);
}
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
public delegate uint ArmouryTapStopDelegate(IntPtr ignored);
'@

$tempDirectory = Join-Path ([IO.Path]::GetTempPath()) (
    'AllyBindings-armoury-runtime-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($tempDirectory) | Out-Null
$testDll = Join-Path $tempDirectory 'AllyBindings.ArmouryTap.dll'
$configPath = "$testDll.config"
$module = [IntPtr]::Zero

$pipe = $null
$stop = $null
$configLock = $null
try {
    Copy-Item $nativeDll $testDll -Force
    if ([ArmouryTapRuntimeNative]::GetModuleHandleW('hid.dll') -ne [IntPtr]::Zero) {
        throw 'The runtime process unexpectedly preloaded hid.dll; the production system-load path was not tested.'
    }
    $pipeName = 'ally-bindings-armoury-runtime-' + [Guid]::NewGuid().ToString('N')
    $token = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($token) } finally { $rng.Dispose() }
    $tokenHex = [BitConverter]::ToString($token).Replace('-', '')
    $config = "pipe=\\.\pipe\$pipeName`ntoken=$tokenHex`nhelper=$PID`n"
    [IO.File]::WriteAllText($configPath, $config, [Text.ASCIIEncoding]::new())
    $configLock = [IO.File]::Open(
        $configPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)

    $assembly = [Reflection.Assembly]::LoadFrom($managedDll)
    Write-Host 'Armoury tap runtime phase: managed Windows assembly loaded.'
    $helperType = $assembly.GetType('AllyBindings.Windows.ArmouryTapCaptureHelper', $true)
    $flags = [Reflection.BindingFlags]'Static, NonPublic'
    $bootIdentifierMethod = $assembly.GetType('AllyBindings.Windows.App', $true).GetMethod(
        'TryGetCurrentBootIdentifier', $flags)
    if ($null -eq $bootIdentifierMethod) { throw 'Windows boot-identifier probe was not found.' }
    $bootIdentifier = $bootIdentifierMethod.Invoke($null, @())
    if ($null -eq $bootIdentifier -or [Guid]$bootIdentifier -eq [Guid]::Empty) {
        throw 'Windows did not return a non-empty kernel boot identifier.'
    }
    Write-Host 'Armoury tap runtime phase: kernel boot identifier passed.'
    $readExportRva = $helperType.GetMethod('ReadExportRva', $flags)
    if ($null -eq $readExportRva) { throw 'Production tap export parser was not found.' }
    $exportArguments = [object[]]@([string]$testDll, [string]'ArmouryTapStop')
    $stopRva = [uint32]$readExportRva.Invoke($null, $exportArguments)
    Write-Host 'Armoury tap runtime phase: production PE export parser returned.'

    $tappedProcessType = $helperType.GetNestedType(
        'TappedProcess', [Reflection.BindingFlags]'NonPublic')
    if ($null -eq $tappedProcessType) { throw 'Production tapped-process type was not found.' }
    $createTapServer = $tappedProcessType.GetMethod('CreateTapServer', $flags)
    $readTapPipeIntegrityLabel = $tappedProcessType.GetMethod('ReadTapPipeIntegrityLabel', $flags)
    if ($null -eq $createTapServer -or $null -eq $readTapPipeIntegrityLabel) {
        throw 'Production tap pipe security methods were not found.'
    }
    $pipe = [IO.Pipes.NamedPipeServerStream]$createTapServer.Invoke($null, @([string]$pipeName))
    $mandatoryLabel = [string]$readTapPipeIntegrityLabel.Invoke($null, @($pipe.SafePipeHandle))
    if ($mandatoryLabel -notmatch 'S:\(ML;;NW;;;(ME|S-1-16-8192)\)') {
        throw "Tap pipe is missing the medium-integrity no-write-up label: $mandatoryLabel"
    }
    $wait = $pipe.WaitForConnectionAsync()
    Write-Host 'Armoury tap runtime phase: loading native module.'
    $module = [ArmouryTapRuntimeNative]::LoadLibraryW($testDll)
    if ($module -eq [IntPtr]::Zero) {
        throw "LoadLibraryW failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }
    if ([ArmouryTapRuntimeNative]::GetModuleHandleW('hid.dll') -eq [IntPtr]::Zero) {
        throw 'The tap PE dependency did not cause Windows to map hid.dll before hook installation.'
    }
    Write-Host 'Armoury tap runtime phase: native module and HID dependency loaded.'
    $systemStopAddress = [ArmouryTapRuntimeNative]::GetProcAddress($module, 'ArmouryTapStop')
    if ($systemStopAddress -eq [IntPtr]::Zero) { throw 'ArmouryTapStop export was not found.' }
    $stop = [Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
        $systemStopAddress, [type][ArmouryTapStopDelegate])
    $stopAddress = [IntPtr]::new($module.ToInt64() + [int64]$stopRva)
    if ($stopAddress -ne $systemStopAddress) {
        throw 'Production tap export parser did not match GetProcAddress.'
    }
    Write-Host 'Armoury tap runtime phase: managed probes and export resolution passed.'
    $stop = [Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
        $stopAddress, [type][ArmouryTapStopDelegate])
    if (-not $wait.Wait([TimeSpan]::FromSeconds(10))) { throw 'Tap DLL did not connect to its configured pipe.' }

    $coreAssembly = [Reflection.Assembly]::LoadFrom($coreDll)
    $protocolType = $coreAssembly.GetType('AllyBindings.Core.ArmouryTapProtocol', $true)
    $wireRecordSize = [int]$protocolType.GetField('WireRecordSize').GetRawConstantValue()
    $record = [byte[]]::new($wireRecordSize)
    $offset = 0
    $readDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while ($offset -lt $record.Length) {
        $remaining = $readDeadline - [DateTime]::UtcNow
        if ($remaining -le [TimeSpan]::Zero) {
            throw "Timed out after reading $offset of $($record.Length) ready bytes."
        }
        $readTask = $pipe.ReadAsync($record, $offset, $record.Length - $offset)
        if (-not $readTask.Wait($remaining)) {
            throw "Timed out after reading $offset of $($record.Length) ready bytes."
        }
        $read = $readTask.GetAwaiter().GetResult()
        if ($read -eq 0) { throw "Tap DLL closed the pipe after $offset of $($record.Length) ready bytes." }
        $offset += $read
    }
    if ([BitConverter]::ToUInt32($record, 0) -ne 0x31544241) { throw 'Tap ready record magic mismatch.' }
    if ([BitConverter]::ToUInt16($record, 4) -ne 2) { throw 'Tap ready record version mismatch.' }
    if ($record[6] -ne 0 -or $record[7] -ne 0) { throw 'Tap ready record API/report-length fields must both be zero.' }
    for ($index = 0; $index -lt $token.Length; $index++) {
        if ($record[28 + $index] -ne $token[$index]) { throw 'Tap ready record capability token mismatch.' }
    }

    Write-Host 'Armoury tap runtime phase: authenticated ready record passed; stopping hooks.'
    # Arm a read before waiting for Stop: the worker flushes its authenticated summary
    # before exiting, so waiting first would deadlock against FlushFileBuffers.
    $summary = [byte[]]::new($wireRecordSize)
    $initialSummaryRead = $pipe.ReadAsync($summary, 0, $summary.Length)
    if ($stop.Invoke([IntPtr]::Zero) -ne 1) { throw 'ArmouryTapStop did not confirm clean hook teardown.' }
    if (-not $initialSummaryRead.Wait([TimeSpan]::FromSeconds(10))) {
        throw 'Timed out waiting for the tap diagnostic summary.'
    }
    $offset = $initialSummaryRead.GetAwaiter().GetResult()
    if ($offset -eq 0) { throw 'Tap DLL closed the pipe before its diagnostic summary.' }
    while ($offset -lt $summary.Length) {
        $read = $pipe.Read($summary, $offset, $summary.Length - $offset)
        if ($read -eq 0) { throw 'Tap DLL closed the pipe before its diagnostic summary.' }
        $offset += $read
    }
    if ([BitConverter]::ToUInt32($summary, 0) -ne 0x31544241 -or
        [BitConverter]::ToUInt16($summary, 4) -ne 2 -or $summary[6] -ne 0xFE -or $summary[7] -ne 0) {
        throw 'Tap diagnostic summary framing mismatch.'
    }
    if ($summary[60] -ne 1 -or $summary[61] -ne 0) { throw 'Tap diagnostic summary schema mismatch.' }
    for ($index = 62; $index -lt $summary.Length; $index++) {
        if ($summary[$index] -ne 0) { throw 'Zero-activity tap diagnostic summary contained a nonzero counter or reserved byte.' }
    }
    Write-Host 'Armoury tap runtime phase: hook teardown passed; unloading native module.'
    if (-not [ArmouryTapRuntimeNative]::FreeLibrary($module)) {
        throw "FreeLibrary failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }
    $module = [IntPtr]::Zero
    if ([ArmouryTapRuntimeNative]::GetModuleHandleW('hid.dll') -ne [IntPtr]::Zero) {
        throw 'The tap did not release the system hid.dll reference it acquired for hook installation.'
    }
    Write-Host 'Armoury tap runtime phase: native module and HID dependency unloaded; checking ACLs.'

    $openToken = $helperType.GetMethod('OpenParentImpersonationToken', $flags)
    $maximumAccess = $helperType.GetMethod('GetMaximumAllowedAccess', $flags)
    if ($null -eq $openToken -or $null -eq $maximumAccess) { throw 'ACL verification methods were not found.' }
    $accessToken = $openToken.Invoke($null, @([Diagnostics.Process]::GetCurrentProcess().Id))
    try {
        $user = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $system = [Security.Principal.SecurityIdentifier]::new(
            [Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
        $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [Security.AccessControl.InheritanceFlags]::ObjectInherit
        $propagation = [Security.AccessControl.PropagationFlags]::None
        $allow = [Security.AccessControl.AccessControlType]::Allow

        $readOnly = [Security.AccessControl.DirectorySecurity]::new()
        $readOnly.SetAccessRuleProtection($true, $false)
        $readOnly.SetOwner($system)
        $readOnly.SetGroup($system)
        $readOnly.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $user, [Security.AccessControl.FileSystemRights]::ReadAndExecute,
            $inheritance, $propagation, $allow))
        $readGranted = [uint32]$maximumAccess.Invoke($null, @($readOnly, $accessToken))
        $dangerous = [uint32](0x00000002 -bor 0x00000004 -bor 0x00000040 -bor
            0x00010000 -bor 0x00040000 -bor 0x00080000)
        if (($readGranted -band $dangerous) -ne 0) { throw 'Read-only ACL was incorrectly classified as writable.' }

        $writable = [Security.AccessControl.DirectorySecurity]::new()
        $writable.SetAccessRuleProtection($true, $false)
        $writable.SetOwner($system)
        $writable.SetGroup($system)
        $writable.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $user, [Security.AccessControl.FileSystemRights]::Modify,
            $inheritance, $propagation, $allow))
        $writeGranted = [uint32]$maximumAccess.Invoke($null, @($writable, $accessToken))
        if (($writeGranted -band $dangerous) -eq 0) { throw 'Writable ACL was not classified as writable.' }
    }
    finally { $accessToken.Dispose() }
    Write-Host 'Armoury tap runtime phase: ACL checks passed.'
}
finally {
    if ($module -ne [IntPtr]::Zero -and $null -ne $stop) {
        try { $stop.Invoke([IntPtr]::Zero) | Out-Null }
        finally { [ArmouryTapRuntimeNative]::FreeLibrary($module) | Out-Null }
    }
    if ($null -ne $pipe) { $pipe.Dispose() }
    if ($null -ne $configLock) { $configLock.Dispose() }

    if (Test-Path $tempDirectory) { Remove-Item $tempDirectory -Recurse -Force }
}

Write-Host 'Armoury tap config, authenticated ready/stop, unload and AccessCheck runtime tests passed.'
