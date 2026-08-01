using System.Buffers.Binary;
using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class UsbPcapHidFeatureReportParserTests
{
    [Fact]
    public void Extracts_outbound_hid_feature_report_with_exact_payload()
    {
        var mapping = AsusRearButtonProtocol.BuildMappingReport(ControllerButton.A, ControllerButton.B);
        var wirePayload = new byte[64];
        mapping.CopyTo(wirePayload, 0);
        using var capture = BuildCapture(BuildControlPacket(wirePayload));

        var result = UsbPcapHidFeatureReportParser.Parse(capture);

        var report = Assert.Single(result.Reports);
        Assert.Equal((byte)0x5A, report.ReportId);
        Assert.Equal((byte)0x03, report.ReportType);
        Assert.Equal((ushort)64, report.DeclaredLength);
        Assert.Equal(64, report.CapturedLength);
        Assert.True(report.LengthMatchesDeclared);
        Assert.Equal((ushort)4, report.Bus);
        Assert.Equal((ushort)7, report.Device);
        Assert.Equal((ushort)2, report.InterfaceNumber);
        Assert.True(report.PayloadReportIdMatches);
        Assert.Equal(Convert.ToHexString(wirePayload), report.PayloadHex);
        Assert.Equal("21095A0302004000", report.SetupHex);
        Assert.Equal(1, result.RecordCount);
        Assert.Equal(0, result.TruncatedRecordCount);
    }

    [Fact]
    public void Preserves_extra_captured_bytes_and_marks_declared_length_mismatch()
    {
        var payload = new byte[65];
        payload[0] = 0x5A;
        payload[^1] = 0xCC;
        using var capture = BuildCapture(BuildControlPacket(payload, declaredLength: 64));

        var result = UsbPcapHidFeatureReportParser.Parse(capture);

        var report = Assert.Single(result.Reports);
        Assert.Equal((ushort)64, report.DeclaredLength);
        Assert.Equal(65, report.CapturedLength);
        Assert.False(report.LengthMatchesDeclared);
        Assert.EndsWith("CC", report.PayloadHex, StringComparison.Ordinal);
    }

    [Fact]
    public void Ignores_non_control_and_non_feature_traffic()
    {
        var mapping = AsusRearButtonProtocol.BuildMappingReport(ControllerButton.X, ControllerButton.Y);
        var interruptPacket = BuildControlPacket(mapping);
        interruptPacket[22] = 1;
        var outputReportPacket = BuildControlPacket(mapping, reportType: 2);
        var inputRequestPacket = BuildControlPacket(mapping);
        inputRequestPacket[28] = 0xA1;
        using var capture = BuildCapture(interruptPacket, outputReportPacket, inputRequestPacket);

        var result = UsbPcapHidFeatureReportParser.Parse(capture);

        Assert.Empty(result.Reports);
        Assert.Equal(3, result.RecordCount);
    }

    [Fact]
    public void Skips_snaplen_truncated_feature_report_instead_of_returning_partial_bytes()
    {
        var payload = new byte[64];
        payload[0] = 0x5A;
        var packet = BuildControlPacket(payload);
        Array.Resize(ref packet, packet.Length - 5);
        using var capture = BuildCaptureRecord(packet, (uint)(packet.Length + 5));

        var result = UsbPcapHidFeatureReportParser.Parse(capture);

        Assert.Empty(result.Reports);
        Assert.Equal(1, result.TruncatedRecordCount);
    }

    [Fact]
    public void Rejects_non_usbpcap_link_type()
    {
        using var capture = BuildCapture();
        capture.Position = 20;
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        capture.Write(bytes);
        capture.Position = 0;

        var exception = Assert.Throws<InvalidDataException>(() =>
            UsbPcapHidFeatureReportParser.Parse(capture));

        Assert.Contains("link type 249", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildControlPacket(
        byte[] payload,
        byte reportType = 3,
        ushort? declaredLength = null)
    {
        const ushort headerLength = 28;
        var dataLength = 8 + payload.Length;
        var packet = new byte[headerLength + dataLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, headerLength);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(2), 123);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(10), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14), 0x001B);
        packet[16] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(17), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(19), 7);
        packet[21] = 0;
        packet[22] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(23), (uint)dataLength);
        packet[27] = 0;
        packet[28] = 0x21;
        packet[29] = 0x09;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(30), (ushort)((reportType << 8) | 0x5A));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(34), declaredLength ?? (ushort)payload.Length);
        payload.CopyTo(packet, 36);
        return packet;
    }

    private static MemoryStream BuildCapture(params byte[][] packets)
    {
        var stream = WriteGlobalHeader();
        foreach (var packet in packets)
        {
            WriteRecord(stream, packet, (uint)packet.Length);
        }
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream BuildCaptureRecord(byte[] packet, uint originalLength)
    {
        var stream = WriteGlobalHeader();
        WriteRecord(stream, packet, originalLength);
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream WriteGlobalHeader()
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(0xA1B2C3D4u);
        writer.Write((ushort)2);
        writer.Write((ushort)4);
        writer.Write(0);
        writer.Write(0u);
        writer.Write(65535u);
        writer.Write(249u);
        return stream;
    }

    private static void WriteRecord(Stream stream, byte[] packet, uint originalLength)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(1_700_000_000u);
        writer.Write(123_456u);
        writer.Write((uint)packet.Length);
        writer.Write(originalLength);
        writer.Write(packet);
    }
}
