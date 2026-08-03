param(
    [string]$NativeDllPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'native/ArmouryTap/build/Release/AllyBindings.ArmouryTap.dll'),
    [string]$ManagedAssemblyPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/AllyBindings.Windows/bin/Release/net8.0-windows10.0.19041.0/win-x64/AllyBindings.dll'),
    [string]$ExecutablePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/AllyBindings-win-x64/AllyBindings.exe')
)

$ErrorActionPreference = 'Stop'
$resourceName = 'AllyBindings.Windows.Native.AllyBindings.ArmouryTap.dll'

foreach ($path in @($NativeDllPath, $ManagedAssemblyPath, $ExecutablePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required embedded-resource test input is missing: $path"
    }
}

$nativeBytes = [IO.File]::ReadAllBytes($NativeDllPath)
if ($nativeBytes.Length -eq 0) { throw 'Native Armoury tap DLL is empty.' }
$nativeHash = [Security.Cryptography.SHA256]::HashData($nativeBytes)

if ($IsWindows -or $env:OS -eq 'Windows_NT') {
    $assembly = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $ManagedAssemblyPath).Path)
    $resourceStream = $assembly.GetManifestResourceStream($resourceName)
    if ($null -eq $resourceStream) {
        throw "Managed assembly is missing native tap resource '$resourceName'."
    }
    try {
        $resourceBytes = New-Object byte[] ([int]$resourceStream.Length)
        $offset = 0
        while ($offset -lt $resourceBytes.Length) {
            $read = $resourceStream.Read($resourceBytes, $offset, $resourceBytes.Length - $offset)
            if ($read -eq 0) { throw 'Embedded native tap resource ended unexpectedly.' }
            $offset += $read
        }
    }
    finally {
        $resourceStream.Dispose()
    }

    $resourceHash = [Security.Cryptography.SHA256]::HashData($resourceBytes)
    if (-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals($nativeHash, $resourceHash)) {
        throw 'Managed native tap resource does not match the built DLL.'
    }
}

if (-not ('EmbeddedResourceProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
public static class EmbeddedResourceProbe
{
    public static bool Contains(byte[] haystack, byte[] needle)
    {
        return haystack.AsSpan().IndexOf(needle) >= 0;
    }
}
'@
}

$executableBytes = [IO.File]::ReadAllBytes($ExecutablePath)
if (-not [EmbeddedResourceProbe]::Contains($executableBytes, $nativeBytes)) {
    throw 'Published single-file executable does not contain the native tap DLL bytes.'
}

Write-Output "Embedded Armoury tap resource validated: $($nativeBytes.Length) bytes; SHA-256 $([Convert]::ToHexString($nativeHash))."
