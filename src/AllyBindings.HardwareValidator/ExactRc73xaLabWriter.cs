using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HidSharp;
using HidSharp.Reports;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AllyBindings.HardwareValidator;

/// <summary>
/// Exact RC73XA lab transport. The sole mutation accepts only the already-built
/// pinned wire packet and performs one SET_FEATURE through a same-handle-validated
/// VID_0B05/PID_1B4C handle. There is no readback, reset, arbitrary mapping, or retry.
/// </summary>
internal static class ExactRc73xaLabWriter
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(3);
    private const string AsusManufacturer = "ASUSTeK COMPUTER INC.";

    internal static async Task<LabTargetSnapshot> InspectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = Task.Run(Inspect, CancellationToken.None);
        if (await Task.WhenAny(operation, Task.Delay(OperationTimeout, cancellationToken)).ConfigureAwait(false) == operation)
            return await operation.ConfigureAwait(false);
        ObserveLateFailure(operation);
        cancellationToken.ThrowIfCancellationRequested();
        return new(false, ReadProductName(), string.Empty, 0, "Target inspection timed out; no hardware write was attempted.");
    }

    internal static async Task<LabWriteResult> WriteAsync(
        LabTargetSnapshot approvedTarget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvedTarget);
        if (!approvedTarget.Approved || string.IsNullOrWhiteSpace(approvedTarget.InterfaceIdentityKey))
            throw new ArgumentException("An approved exact-target snapshot is required.", nameof(approvedTarget));

        cancellationToken.ThrowIfCancellationRequested();
        var immutablePacket = HardwareLabPolicy.BuildWirePacket(approvedTarget.FeatureReportLength);
        var operation = Task.Run(
            () => WritePinnedHandle(
                immutablePacket,
                approvedTarget.InterfaceIdentityKey,
                approvedTarget.FeatureReportLength),
            CancellationToken.None);
        if (await Task.WhenAny(operation, Task.Delay(OperationTimeout, cancellationToken)).ConfigureAwait(false) == operation)
            return await operation.ConfigureAwait(false);

        ObserveLateFailure(operation);
        cancellationToken.ThrowIfCancellationRequested();
        return new(1, 0, "The pinned RC73XA HID write timed out; its outcome is unknown and Armoury recovery remains required.");
    }

    private static LabTargetSnapshot Inspect()
    {
        var model = ReadProductName();
        if (!IsExactSystemIdentity())
            return new(false, model, string.Empty, 0, "System DMI is not the exact approved ASUS RC73XA identity.");

        var candidates = FindExactCandidates();
        if (candidates.Count != 1)
            return new(false, model, string.Empty, 0, $"Expected exactly one RC73XA/PID_1B4C report-0x5A interface; found {candidates.Count}.");

        var device = candidates[0];
        using var handle = OpenValidatedHandle(device, out var featureReportLength, out var failure);
        if (handle is null)
            return new(false, model, string.Empty, 0, failure);

        return new(
            true,
            model,
            BuildInterfaceIdentityKey(device),
            featureReportLength,
            "Exact ASUS RC73XA DMI, VID_0B05/PID_1B4C, report-0x5A descriptor, and native handle caps validated without reading a feature report.");
    }

    private static LabWriteResult WritePinnedHandle(
        byte[] fixedWirePacket,
        string expectedInterfaceIdentityKey,
        int expectedFeatureReportLength)
    {
        if (!IsExactSystemIdentity())
            return new(0, 0, "Exact ASUS RC73XA DMI identity changed after confirmation; no write was attempted.");

        var candidates = FindExactCandidates();
        if (candidates.Count != 1)
            return new(0, 0, $"Expected exactly one RC73XA/PID_1B4C interface at write time; found {candidates.Count}.");

        var device = candidates[0];
        if (!BuildInterfaceIdentityKey(device).Equals(expectedInterfaceIdentityKey, StringComparison.Ordinal))
            return new(0, 0, "Exact HID interface identity changed after confirmation; no write was attempted.");

        using var handle = OpenValidatedHandle(device, out var featureReportLength, out var failure);
        if (handle is null)
            return new(0, 0, failure);
        if (featureReportLength != expectedFeatureReportLength || fixedWirePacket.Length != featureReportLength)
            return new(0, 0, "Feature-report length changed after confirmation; no write was attempted.");

        // DMI is checked again while the validated native handle remains pinned.
        if (!IsExactSystemIdentity())
            return new(0, 0, "Exact ASUS RC73XA DMI identity changed while the HID handle was pinned; no write was attempted.");

        if (!NativeMethods.HidD_SetFeature(handle, fixedWirePacket, fixedWirePacket.Length))
            return new(1, 0, $"The pinned VID_0B05/PID_1B4C handle rejected SET_FEATURE ({Marshal.GetLastWin32Error()}); Armoury recovery remains required.");

        return new(1, 1, "The same-handle-validated VID_0B05/PID_1B4C interface accepted the sole fixed M1=A/M2=B SET_FEATURE call.");
    }

    private static SafeFileHandle? OpenValidatedHandle(
        HidDevice device,
        out int featureReportLength,
        out string failure)
    {
        featureReportLength = 0;
        var handle = NativeMethods.CreateFileW(
            device.GetFileSystemName(),
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            failure = $"Exact RC73XA HID handle could not be opened ({Marshal.GetLastWin32Error()}).";
            handle.Dispose();
            return null;
        }

        var attributes = new NativeMethods.HiddAttributes { Size = Marshal.SizeOf<NativeMethods.HiddAttributes>() };
        if (!NativeMethods.HidD_GetAttributes(handle, ref attributes) ||
            attributes.VendorId != HardwareLabPolicy.TargetVendorId ||
            attributes.ProductId != HardwareLabPolicy.TargetProductId)
        {
            failure = "Opened HID handle did not revalidate as VID_0B05/PID_1B4C.";
            handle.Dispose();
            return null;
        }

        if (!TryGetCaps(handle, out var caps) ||
            !HardwareLabPolicy.IsApprovedInterface(
                attributes.VendorId,
                attributes.ProductId,
                caps.FeatureReportByteLength,
                caps.FeatureReportByteLength))
        {
            failure = "Opened HID handle feature caps were outside the bounded 50-64-byte contract.";
            handle.Dispose();
            return null;
        }

        featureReportLength = caps.FeatureReportByteLength;
        failure = string.Empty;
        return handle;
    }

    private static List<HidDevice> FindExactCandidates()
    {
        var candidates = new List<HidDevice>();
        foreach (var device in DeviceList.Local.GetHidDevices(HardwareLabPolicy.TargetVendorId, HardwareLabPolicy.TargetProductId))
        {
            try
            {
                if (device.GetReportDescriptor().TryGetReport(
                        ReportType.Feature,
                        HardwareLabPolicy.FeatureReportId,
                        out var featureReport) &&
                    HardwareLabPolicy.IsApprovedInterface(
                        device.VendorID,
                        device.ProductID,
                        device.GetMaxFeatureReportLength(),
                        featureReport.Length))
                    candidates.Add(device);
            }
            catch (Exception)
            {
                // A disappearing or unreadable interface is never an approved candidate.
            }
        }
        return candidates.OrderBy(device => device.GetFileSystemName(), StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool TryGetCaps(SafeFileHandle handle, out NativeMethods.HidpCaps caps)
    {
        caps = new NativeMethods.HidpCaps { Reserved = new ushort[17] };
        if (!NativeMethods.HidD_GetPreparsedData(handle, out var preparsedData)) return false;
        try { return NativeMethods.HidP_GetCaps(preparsedData, ref caps) == NativeMethods.HidpStatusSuccess; }
        finally { _ = NativeMethods.HidD_FreePreparsedData(preparsedData); }
    }

    private static bool IsExactSystemIdentity()
    {
        try
        {
            var manufacturer = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer", null) as string;
            return manufacturer?.Trim().Equals(AsusManufacturer, StringComparison.OrdinalIgnoreCase) == true &&
                   HardwareLabPolicy.IsApprovedProductName(ReadProductName());
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ReadProductName()
    {
        try
        {
            return (Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName", null) as string)?.Trim() ?? "Unknown";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return "Unknown";
        }
    }

    private static string BuildInterfaceIdentityKey(HidDevice device)
    {
        var normalizedPath = device.GetFileSystemName().Trim().ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
    }

    private static void ObserveLateFailure(Task operation)
    {
        _ = operation.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static class NativeMethods
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint FileShareRead = 1;
        internal const uint FileShareWrite = 2;
        internal const uint OpenExisting = 3;
        internal const int HidpStatusSuccess = 0x00110000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps caps);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_SetFeature(SafeFileHandle device, byte[] reportBuffer, int reportBufferLength);

        [StructLayout(LayoutKind.Sequential)]
        internal struct HiddAttributes
        {
            internal int Size;
            internal ushort VendorId;
            internal ushort ProductId;
            internal ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HidpCaps
        {
            internal ushort Usage;
            internal ushort UsagePage;
            internal ushort InputReportByteLength;
            internal ushort OutputReportByteLength;
            internal ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
            internal ushort NumberLinkCollectionNodes;
            internal ushort NumberInputButtonCaps;
            internal ushort NumberInputValueCaps;
            internal ushort NumberInputDataIndices;
            internal ushort NumberOutputButtonCaps;
            internal ushort NumberOutputValueCaps;
            internal ushort NumberOutputDataIndices;
            internal ushort NumberFeatureButtonCaps;
            internal ushort NumberFeatureValueCaps;
            internal ushort NumberFeatureDataIndices;
        }
    }
}
