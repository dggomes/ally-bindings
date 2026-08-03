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
$hidModule = [IntPtr]::Zero
$pipe = $null
$stop = $null
try {
    Copy-Item $nativeDll $testDll -Force
    $hidModule = [ArmouryTapRuntimeNative]::LoadLibraryW('hid.dll')
    if ($hidModule -eq [IntPtr]::Zero) { throw 'The runtime test could not load hid.dll for the complete two-hook contract.' }
    $pipeName = 'ally-bindings-armoury-runtime-' + [Guid]::NewGuid().ToString('N')
    $token = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($token) } finally { $rng.Dispose() }
    $tokenHex = [BitConverter]::ToString($token).Replace('-', '')
    $config = "pipe=\\.\pipe\$pipeName`ntoken=$tokenHex`nhelper=$PID`n"
    [IO.File]::WriteAllText($configPath, $config, [Text.ASCIIEncoding]::new())

    $pipe = [IO.Pipes.NamedPipeServerStream]::new(
        $pipeName,
        [IO.Pipes.PipeDirection]::In,
        1,
        [IO.Pipes.PipeTransmissionMode]::Byte,
        [IO.Pipes.PipeOptions]::Asynchronous)
    $wait = $pipe.WaitForConnectionAsync()
    $module = [ArmouryTapRuntimeNative]::LoadLibraryW($testDll)
    if ($module -eq [IntPtr]::Zero) {
        throw "LoadLibraryW failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }
    $systemStopAddress = [ArmouryTapRuntimeNative]::GetProcAddress($module, 'ArmouryTapStop')
    if ($systemStopAddress -eq [IntPtr]::Zero) { throw 'ArmouryTapStop export was not found.' }
    $stop = [Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
        $systemStopAddress, [type][ArmouryTapStopDelegate])
    $assembly = [Reflection.Assembly]::LoadFrom($managedDll)
    $helperType = $assembly.GetType('AllyBindings.Windows.ArmouryTapCaptureHelper', $true)
    $flags = [Reflection.BindingFlags]'Static, NonPublic'
    $readExportRva = $helperType.GetMethod('ReadExportRva', $flags)
    if ($null -eq $readExportRva) { throw 'Production tap export parser was not found.' }
    $stopRva = [uint32]$readExportRva.Invoke($null, @($testDll, 'ArmouryTapStop'))
    $stopAddress = [IntPtr]::new($module.ToInt64() + [int64]$stopRva)
    if ($stopAddress -ne $systemStopAddress) {
        throw 'Production tap export parser did not match GetProcAddress.'
    }
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
    if ([BitConverter]::ToUInt16($record, 4) -ne 1) { throw 'Tap ready record version mismatch.' }
    if ($record[6] -ne 0 -or $record[7] -ne 0) { throw 'Tap ready record API/report-length fields must both be zero.' }
    for ($index = 0; $index -lt $token.Length; $index++) {
        if ($record[28 + $index] -ne $token[$index]) { throw 'Tap ready record capability token mismatch.' }
    }

    if ($stop.Invoke([IntPtr]::Zero) -ne 1) { throw 'ArmouryTapStop did not confirm clean hook teardown.' }
    if (-not [ArmouryTapRuntimeNative]::FreeLibrary($module)) {
        throw "FreeLibrary failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }
    $module = [IntPtr]::Zero

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
}
finally {
    if ($module -ne [IntPtr]::Zero -and $null -ne $stop) {
        try { $stop.Invoke([IntPtr]::Zero) | Out-Null }
        finally { [ArmouryTapRuntimeNative]::FreeLibrary($module) | Out-Null }
    }
    if ($null -ne $pipe) { $pipe.Dispose() }
    if ($hidModule -ne [IntPtr]::Zero) { [ArmouryTapRuntimeNative]::FreeLibrary($hidModule) | Out-Null }
    if (Test-Path $tempDirectory) { Remove-Item $tempDirectory -Recurse -Force }
}

Write-Host 'Armoury tap config, authenticated ready/stop, unload and AccessCheck runtime tests passed.'
