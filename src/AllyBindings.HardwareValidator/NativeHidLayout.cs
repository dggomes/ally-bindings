using System.Runtime.InteropServices;

namespace AllyBindings.HardwareValidator;

internal static class NativeHidLayout
{
    // Windows SDK hidpi.h, x64 ABI. The validator refuses to run as a non-x64 process.
    internal const int ValueCapsSize = 72;
    internal const int ButtonCapsSize = 72;
    internal const int ReportIdOffset = 2;
    internal const int HidpCapsSize = 64;
    internal const int HiddAttributesSize = 12;
    internal const int DeviceInterfaceDataSize = 32;
    internal const int DeviceInterfaceDetailCbSize = 8;
    internal const int DeviceInterfacePathOffset = 4;

    internal static bool ContainsReportId(IntPtr buffer, uint count, int entrySize, byte reportId)
    {
        if (buffer == IntPtr.Zero || entrySize <= ReportIdOffset)
            return false;

        for (uint index = 0; index < count; index++)
        {
            var offset = checked((int)index * entrySize + ReportIdOffset);
            if (Marshal.ReadByte(buffer, offset) == reportId)
                return true;
        }

        return false;
    }
}
