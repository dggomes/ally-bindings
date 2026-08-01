using System.Buffers.Binary;

namespace AllyBindings.Core;

/// <summary>
/// Reads classic PCAP files emitted by USBPcap and extracts only outbound USB
/// HID SET_REPORT(feature) control transfers. It never opens a device and has
/// no write path.
/// </summary>
public static class UsbPcapHidFeatureReportParser
{
    private const uint LittleEndianMicrosecondMagic = 0xA1B2C3D4;
    private const uint LittleEndianNanosecondMagic = 0xA1B23C4D;
    private const uint UsbPcapLinkType = 249;
    private const int GlobalHeaderLength = 24;
    private const int RecordHeaderLength = 16;
    private const int UsbPcapPacketHeaderLength = 27;
    private const byte ControlTransfer = 2;
    private const byte ControlSetupStage = 0;
    private const byte HidSetReport = 0x09;
    private const byte HidFeatureReportType = 0x03;
    private const int MaxPacketLength = 1024 * 1024;
    private const int MaxRecords = 250_000;

    public static UsbPcapParseResult Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The USBPcap stream must be readable.", nameof(stream));
        }

        Span<byte> globalHeader = stackalloc byte[GlobalHeaderLength];
        ReadExactly(stream, globalHeader, "USBPcap global header");
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(globalHeader);
        var nanosecondTimestamps = magic switch
        {
            LittleEndianMicrosecondMagic => false,
            LittleEndianNanosecondMagic => true,
            _ => throw new InvalidDataException("Only little-endian classic USBPcap files are supported."),
        };
        var linkType = BinaryPrimitives.ReadUInt32LittleEndian(globalHeader[20..]);
        if (linkType != UsbPcapLinkType)
        {
            throw new InvalidDataException($"Expected USBPcap link type 249, found {linkType}.");
        }

        var reports = new List<CapturedHidFeatureReport>();
        var recordCount = 0;
        var truncatedRecords = 0;
        Span<byte> recordHeader = stackalloc byte[RecordHeaderLength];
        while (TryReadExactly(stream, recordHeader))
        {
            recordCount++;
            if (recordCount > MaxRecords)
            {
                throw new InvalidDataException($"USBPcap file exceeds the {MaxRecords:N0}-record safety limit.");
            }

            var timestampSeconds = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader);
            var timestampFraction = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader[4..]);
            var includedLength = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader[8..]);
            var originalLength = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader[12..]);
            if (includedLength > MaxPacketLength)
            {
                throw new InvalidDataException($"USBPcap record length {includedLength} exceeds the safety limit.");
            }

            var packet = new byte[checked((int)includedLength)];
            ReadExactly(stream, packet, "USBPcap packet data");
            if (includedLength < originalLength)
            {
                truncatedRecords++;
            }
            TryExtractFeatureReport(
                packet,
                timestampSeconds,
                timestampFraction,
                nanosecondTimestamps,
                reports);
        }

        return new(reports, recordCount, truncatedRecords);
    }

    private static void TryExtractFeatureReport(
        ReadOnlySpan<byte> packet,
        uint timestampSeconds,
        uint timestampFraction,
        bool nanosecondTimestamps,
        ICollection<CapturedHidFeatureReport> reports)
    {
        if (packet.Length < UsbPcapPacketHeaderLength)
        {
            return;
        }

        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(packet);
        var info = packet[16];
        var bus = BinaryPrimitives.ReadUInt16LittleEndian(packet[17..]);
        var device = BinaryPrimitives.ReadUInt16LittleEndian(packet[19..]);
        var endpoint = packet[21];
        var transfer = packet[22];
        var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(packet[23..]);
        if (headerLength < UsbPcapPacketHeaderLength + 1 ||
            headerLength > packet.Length ||
            transfer != ControlTransfer ||
            endpoint != 0 ||
            (info & 0x01) != 0)
        {
            return;
        }

        var controlStage = packet[UsbPcapPacketHeaderLength];
        if (controlStage != ControlSetupStage || dataLength < 8 || dataLength > packet.Length - headerLength)
        {
            return;
        }

        var transferData = packet.Slice(headerLength, checked((int)dataLength));
        var setup = transferData[..8];
        var requestType = setup[0];
        var request = setup[1];
        var value = BinaryPrimitives.ReadUInt16LittleEndian(setup[2..]);
        var interfaceNumber = BinaryPrimitives.ReadUInt16LittleEndian(setup[4..]);
        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(setup[6..]);
        var reportType = (byte)(value >> 8);
        var reportId = (byte)value;
        if (requestType != 0x21 || request != HidSetReport || reportType != HidFeatureReportType)
        {
            return;
        }

        var payload = transferData[8..];

        var ticksPerFraction = nanosecondTimestamps ? TimeSpan.TicksPerSecond / 1_000_000_000d : 10d;
        var timestamp = DateTimeOffset.UnixEpoch
            .AddSeconds(timestampSeconds)
            .AddTicks((long)Math.Round(timestampFraction * ticksPerFraction));
        reports.Add(new(
            timestamp,
            bus,
            device,
            interfaceNumber,
            reportType,
            reportId,
            declaredLength,
            payload.Length,
            payload.Length == declaredLength,
            payload.Length > 0 && payload[0] == reportId,
            Convert.ToHexString(setup),
            Convert.ToHexString(payload)));
    }

    private static bool TryReadExactly(Stream stream, Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = stream.Read(destination[offset..]);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return false;
                }
                throw new InvalidDataException("USBPcap file ended inside a record header.");
            }
            offset += read;
        }
        return true;
    }

    private static void ReadExactly(Stream stream, Span<byte> destination, string part)
    {
        if (!TryReadExactly(stream, destination))
        {
            throw new InvalidDataException($"USBPcap file is missing its {part}.");
        }
    }
}

public sealed record CapturedHidFeatureReport(
    DateTimeOffset Timestamp,
    ushort Bus,
    ushort Device,
    ushort InterfaceNumber,
    byte ReportType,
    byte ReportId,
    ushort DeclaredLength,
    int CapturedLength,
    bool LengthMatchesDeclared,
    bool PayloadReportIdMatches,
    string SetupHex,
    string PayloadHex);

public sealed record UsbPcapParseResult(
    IReadOnlyList<CapturedHidFeatureReport> Reports,
    int RecordCount,
    int TruncatedRecordCount);
