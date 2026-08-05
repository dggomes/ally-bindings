using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using AllyBindings.SoftwareProbe;

namespace AllyBindings.M1M2Probe;

internal static class WindowsCapabilities
{
    internal static SoftwareProbeCapabilities Inspect()
    {
        EnsureWindows();
        var slots = ImmutableArray.CreateBuilder<int>();
        for (var index = 0; index < 4; index++)
        {
            try
            {
                if (XInputGetState((uint)index, out _) == 0) slots.Add(index);
            }
            catch (DllNotFoundException)
            {
                break;
            }
            catch (EntryPointNotFoundException)
            {
                break;
            }
        }

        var vigem = InspectService(["ViGEmBus"]);
        var hidhide = InspectService(["HidHide", "HidHideService"]);
        return new(
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "unknown",
            RuntimeInformation.OSDescription,
            ReadRegistryString(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName") ?? "unknown",
            slots.ToImmutable(),
            vigem.Installed,
            hidhide.Installed,
            vigem.Status,
            hidhide.Status);
    }

    internal static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The M1/M2 software probe runs only on Windows.");
    }

    private static (bool Installed, string Status) InspectService(IReadOnlyList<string> serviceNames)
    {
        foreach (var serviceName in serviceNames)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key is null) continue;

            var manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) return (true, "Installed; service state unavailable");
            try
            {
                var service = OpenService(manager, serviceName, ServiceQueryStatus);
                if (service == IntPtr.Zero) return (true, "Installed; service state unavailable");
                try
                {
                    var status = new ServiceStatusProcess();
                    var size = Marshal.SizeOf<ServiceStatusProcess>();
                    return QueryServiceStatusEx(service, 0, ref status, size, out _)
                        ? (true, StateLabel(status.CurrentState))
                        : (true, "Installed; service state unavailable");
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(manager);
            }
        }
        return (false, "Not installed");
    }

    private static string StateLabel(uint state) => state switch
    {
        1 => "Stopped",
        2 => "Starting",
        3 => "Stopping",
        4 => "Running",
        5 => "Continuing",
        6 => "Pausing",
        7 => "Paused",
        _ => $"Unknown ({state})",
    };

    private static string? ReadRegistryString(string keyPath, string valueName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue(valueName) as string;
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftThumbX;
        public short LeftThumbY;
        public short RightThumbX;
        public short RightThumbY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service,
        int infoLevel,
        ref ServiceStatusProcess buffer,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
