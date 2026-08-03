using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AllyBindings.HardwareValidator;

internal static class ExactRc73xaLabWriter
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const int HidpFeature = 2;
    private const int HidpStatusSuccess = 0x00110000;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    internal static Task<LabTargetSnapshot> InspectAsync(CancellationToken cancellationToken) =>
        Task.Run(Inspect, cancellationToken);

    internal static async Task<LabWriteResult> WriteAsync(
        HardwareLabPolicy.ApprovedOperation approvedOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvedOperation);
        var target = approvedOperation.Target;
        if (!target.Approved || string.IsNullOrWhiteSpace(target.InterfaceIdentityKey))
            throw new ArgumentException("An approved exact-target operation is required.", nameof(approvedOperation));

        cancellationToken.ThrowIfCancellationRequested();
        var immutablePacket = PrepareFixedWirePacket(approvedOperation, target.FeatureReportLength);
        var operation = Task.Run(
            () => WritePinnedHandle(immutablePacket, target.InterfaceIdentityKey, target.FeatureReportLength),
            CancellationToken.None);
        if (await Task.WhenAny(operation, Task.Delay(OperationTimeout, cancellationToken)).ConfigureAwait(false) == operation)
            return await operation.ConfigureAwait(false);

        _ = operation.ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        throw new TimeoutException("The single HID operation did not finish within five seconds; outcome is unknown.");
    }

    private static LabTargetSnapshot Inspect()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || !HasExpectedNativeLayout())
            return Rejected("The controlled validator requires 64-bit Windows.");

        var (manufacturer, productName) = ReadDmiIdentity();
        if (!manufacturer.Equals("ASUSTeK COMPUTER INC.", StringComparison.OrdinalIgnoreCase) ||
            !HardwareLabPolicy.IsApprovedProductName(productName))
        {
            return Rejected("DMI identity is not the exact approved ASUS RC73XA target.", productName);
        }

        var candidates = EnumerateValidatedCandidates(desiredAccess: 0);
        if (candidates.Count != 1)
            return Rejected($"Expected exactly one same-handle validated PID_1B4C/report-0x5A interface; found {candidates.Count}.", productName);

        var candidate = candidates[0];
        return new(
            true,
            productName,
            candidate.IdentityKey,
            candidate.FeatureReportLength,
            "Exact RC73XA/PID_1B4C/report-0x5A target approved using native same-handle descriptor validation.");
    }

    private static LabWriteResult WritePinnedHandle(
        byte[] fixedWirePacket,
        string expectedInterfaceIdentityKey,
        int expectedFeatureReportLength)
    {
        var (manufacturer, productName) = ReadDmiIdentity();
        if (!manufacturer.Equals("ASUSTeK COMPUTER INC.", StringComparison.OrdinalIgnoreCase) ||
            !HardwareLabPolicy.IsApprovedProductName(productName))
        {
            return new(0, 0, "DMI identity changed before the write.");
        }

        var candidates = EnumerateValidatedCandidates(desiredAccess: 0);
        if (candidates.Count != 1 ||
            !string.Equals(candidates[0].IdentityKey, expectedInterfaceIdentityKey, StringComparison.Ordinal) ||
            candidates[0].FeatureReportLength != expectedFeatureReportLength)
        {
            return new(0, 0, "Exact target topology or descriptor identity changed before the write.");
        }

        using var handle = OpenDevice(candidates[0].Path, GenericWrite);
        if (handle.IsInvalid ||
            !TryValidateHandle(handle, out var featureReportLength) ||
            featureReportLength != expectedFeatureReportLength ||
            fixedWirePacket.Length != expectedFeatureReportLength ||
            fixedWirePacket[0] != HardwareLabPolicy.FeatureReportId)
        {
            return new(0, 0, "The pinned write handle failed exact VID/PID/report-0x5A/caps revalidation.");
        }

        var expectedPacket = HardwareLabPolicy.BuildWirePacket(featureReportLength);
        if (!CryptographicOperations.FixedTimeEquals(fixedWirePacket, expectedPacket))
            return new(0, 0, "The approved wire packet changed before SET_FEATURE.");

        var accepted = HidD_SetFeature(handle, fixedWirePacket, fixedWirePacket.Length);
        var error = Marshal.GetLastWin32Error();
        GC.KeepAlive(fixedWirePacket);
        return accepted
            ? new(1, 1, "hid-api-accepted")
            : new(1, 0, $"hid-api-rejected-win32-{error}");
    }

    private static List<NativeCandidate> EnumerateValidatedCandidates(uint desiredAccess)
    {
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) return [];

        try
        {
            var results = new List<NativeCandidate>();
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData { Size = NativeHidLayout.DeviceInterfaceDataSize };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    return [];
                }

                _ = SetupDiGetDeviceInterfaceDetailW(set, ref interfaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required < 8 || required > 32_768) continue;

                var detail = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    Marshal.WriteInt32(detail, NativeHidLayout.DeviceInterfaceDetailCbSize);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref interfaceData, detail, required, out _, IntPtr.Zero))
                        continue;

                    var path = Marshal.PtrToStringUni(IntPtr.Add(detail, NativeHidLayout.DeviceInterfacePathOffset));
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    using var handle = OpenDevice(path, desiredAccess);
                    if (handle.IsInvalid || !TryValidateHandle(handle, out var featureReportLength))
                        continue;

                    results.Add(new(path, StableIdentityKey(path), featureReportLength));
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }

            return results;
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static bool TryValidateHandle(SafeFileHandle handle, out int featureReportLength)
    {
        featureReportLength = 0;
        var attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
        if (!HidD_GetAttributes(handle, ref attributes) ||
            attributes.VendorId != HardwareLabPolicy.TargetVendorId ||
            attributes.ProductId != HardwareLabPolicy.TargetProductId ||
            !HidD_GetPreparsedData(handle, out var preparsedData))
        {
            return false;
        }

        try
        {
            var caps = new HidpCaps { Reserved = new ushort[17] };
            if (HidP_GetCaps(preparsedData, ref caps) != HidpStatusSuccess ||
                !HardwareLabPolicy.IsApprovedInterface(
                    attributes.VendorId,
                    attributes.ProductId,
                    caps.FeatureReportByteLength,
                    caps.FeatureReportByteLength) ||
                !HasFeatureReportId(preparsedData, caps, HardwareLabPolicy.FeatureReportId))
            {
                return false;
            }

            featureReportLength = caps.FeatureReportByteLength;
            return true;
        }
        finally
        {
            _ = HidD_FreePreparsedData(preparsedData);
        }
    }

    private static bool HasFeatureReportId(IntPtr preparsedData, HidpCaps caps, byte reportId) =>
        ContainsValueReportId(preparsedData, caps.NumberFeatureValueCaps, reportId) ||
        ContainsButtonReportId(preparsedData, caps.NumberFeatureButtonCaps, reportId);

    private static bool ContainsValueReportId(IntPtr preparsedData, ushort capacity, byte reportId)
    {
        if (capacity == 0 || capacity > 1_024) return false;
        var buffer = Marshal.AllocHGlobal(checked(capacity * NativeHidLayout.ValueCapsSize));
        try
        {
            ushort length = capacity;
            return HidP_GetSpecificValueCaps(HidpFeature, 0, 0, 0, buffer, ref length, preparsedData) == HidpStatusSuccess &&
                   length <= capacity &&
                   NativeHidLayout.ContainsReportId(buffer, length, NativeHidLayout.ValueCapsSize, reportId);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool ContainsButtonReportId(IntPtr preparsedData, ushort capacity, byte reportId)
    {
        if (capacity == 0 || capacity > 1_024) return false;
        var buffer = Marshal.AllocHGlobal(checked(capacity * NativeHidLayout.ButtonCapsSize));
        try
        {
            ushort length = capacity;
            return HidP_GetSpecificButtonCaps(HidpFeature, 0, 0, 0, buffer, ref length, preparsedData) == HidpStatusSuccess &&
                   length <= capacity &&
                   NativeHidLayout.ContainsReportId(buffer, length, NativeHidLayout.ButtonCapsSize, reportId);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static SafeFileHandle OpenDevice(string path, uint desiredAccess) =>
        CreateFileW(path, desiredAccess, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    private static bool HasExpectedNativeLayout() =>
        Marshal.SizeOf<HidpCaps>() == NativeHidLayout.HidpCapsSize &&
        Marshal.SizeOf<HiddAttributes>() == NativeHidLayout.HiddAttributesSize &&
        Marshal.SizeOf<SpDeviceInterfaceData>() == NativeHidLayout.DeviceInterfaceDataSize;

    internal static byte[] PrepareFixedWirePacket(
        HardwareLabPolicy.ApprovedOperation operation,
        int pinnedFeatureReportLength)
    {
        if (operation.Target.FeatureReportLength != pinnedFeatureReportLength)
            throw new InvalidOperationException("Pinned-handle report length diverged from the approved target.");

        var fixedWirePacket = operation.CopyWirePacket();
        var expectedPacket = HardwareLabPolicy.BuildWirePacket(pinnedFeatureReportLength);
        if (fixedWirePacket.Length != pinnedFeatureReportLength ||
            !CryptographicOperations.FixedTimeEquals(fixedWirePacket, expectedPacket))
            throw new InvalidOperationException("Pinned-handle packet diverged from the approved fixed vector.");

        return fixedWirePacket;
    }

    private static LabTargetSnapshot Rejected(string message, string model = "unknown") =>
        new(false, model, string.Empty, 0, message);

    private static (string Manufacturer, string ProductName) ReadDmiIdentity()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS", writable: false);
        return (
            Convert.ToString(key?.GetValue("SystemManufacturer"))?.Trim() ?? string.Empty,
            Convert.ToString(key?.GetValue("SystemProductName"))?.Trim() ?? string.Empty);
    }

    private static string StableIdentityKey(string path)
    {
        var normalized = path.Trim().ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private sealed record NativeCandidate(string Path, string IdentityKey, int FeatureReportLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        internal uint Size;
        internal Guid InterfaceClassGuid;
        internal uint Flags;
        internal UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        internal int Size;
        internal ushort VendorId;
        internal ushort ProductId;
        internal ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        internal ushort Usage;
        internal ushort UsagePage;
        internal ushort InputReportByteLength;
        internal ushort OutputReportByteLength;
        internal ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        internal ushort[] Reserved;
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

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid,
        string? enumerator,
        IntPtr parent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HiddAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps capabilities);

    [DllImport("hid.dll")]
    private static extern int HidP_GetSpecificValueCaps(
        int reportType,
        ushort usagePage,
        ushort linkCollection,
        ushort usage,
        IntPtr valueCaps,
        ref ushort valueCapsLength,
        IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetSpecificButtonCaps(
        int reportType,
        ushort usagePage,
        ushort linkCollection,
        ushort usage,
        IntPtr buttonCaps,
        ref ushort buttonCapsLength,
        IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);
}
