using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Reflection;

namespace AllyBindings.HardwareValidator.Tests;

public sealed class HardwareLabPolicyTests
{
    private const string GoldenHex = "5AD102082C010200000000000000000001020000000000000000000101000000000000000000010100000000000000000000";
    private const string GoldenSha256 = "fb0f2ac8167350edf147fb839be2306ccb15494c824a44badeff7aad083cf38b";

    [Fact]
    public void Logical_command_matches_independent_golden_vector_and_hash()
    {
        var packet = HardwareLabPolicy.GetLogicalPacket();
        Assert.Equal(GoldenHex, Convert.ToHexString(packet));
        Assert.Equal(GoldenSha256, Convert.ToHexString(SHA256.HashData(packet)).ToLowerInvariant());
        Assert.Equal(GoldenSha256, HardwareLabPolicy.LogicalPacketSha256);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(64)]
    public void Wire_packet_is_only_the_fixed_command_plus_zero_padding(int length)
    {
        var packet = HardwareLabPolicy.BuildWirePacket(length);
        Assert.Equal(length, packet.Length);
        Assert.Equal(GoldenHex, Convert.ToHexString(packet[..50]));
        Assert.All(packet[50..], value => Assert.Equal((byte)0, value));
    }

    [Theory]
    [InlineData(49)]
    [InlineData(65)]
    public void Wire_packet_rejects_unreviewed_lengths(int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HardwareLabPolicy.BuildWirePacket(length));

    [Theory]
    [InlineData("RC73XA")]
    [InlineData("rc73xa_rc73xa")]
    [InlineData("ROG Xbox Ally X RC73XA_RC73XA")]
    public void Exact_product_names_are_approved(string productName) =>
        Assert.True(HardwareLabPolicy.IsApprovedProductName(productName));

    [Theory]
    [InlineData("RC71L")]
    [InlineData("RC72LA")]
    [InlineData("RC73YA")]
    [InlineData("ROG Xbox Ally RC73XA_RC73XA")]
    [InlineData(null)]
    public void Every_other_supported_family_model_is_rejected(string? productName) =>
        Assert.False(HardwareLabPolicy.IsApprovedProductName(productName));

    [Theory]
    [InlineData(0x0B05, 0x1B4C, 50, 50, true)]
    [InlineData(0x0B05, 0x1B4C, 64, 64, true)]
    [InlineData(0x0B05, 0x1ABE, 50, 50, false)]
    [InlineData(0x0B06, 0x1B4C, 50, 50, false)]
    [InlineData(0x0B05, 0x1B4C, 49, 50, false)]
    [InlineData(0x0B05, 0x1B4C, 65, 50, false)]
    [InlineData(0x0B05, 0x1B4C, 50, 49, false)]
    [InlineData(0x0B05, 0x1B4C, 50, 65, false)]
    public void Interface_gate_pins_vid_pid_and_bounds(
        int vid, int pid, int maxLength, int descriptorLength, bool expected) =>
        Assert.Equal(expected, HardwareLabPolicy.IsApprovedInterface(vid, pid, maxLength, descriptorLength));

    [Fact]
    public void Authorization_requires_exact_command_phrase_interactive_input_and_one_interface()
    {
        Assert.True(HardwareLabPolicy.Authorize(
            HardwareLabPolicy.WriteCommand,
            HardwareLabPolicy.ConfirmationPhrase,
            inputRedirected: false,
            compatibleInterfaceCount: 1).Approved);
        Assert.False(HardwareLabPolicy.Authorize("write", HardwareLabPolicy.ConfirmationPhrase, false, 1).Approved);
        Assert.False(HardwareLabPolicy.Authorize(HardwareLabPolicy.WriteCommand, "wrong", false, 1).Approved);
        Assert.False(HardwareLabPolicy.Authorize(HardwareLabPolicy.WriteCommand, HardwareLabPolicy.ConfirmationPhrase, true, 1).Approved);
        Assert.False(HardwareLabPolicy.Authorize(HardwareLabPolicy.WriteCommand, HardwareLabPolicy.ConfirmationPhrase, false, 2).Approved);
    }

    [Fact]
    public void Approved_operation_binds_displayed_hashed_audited_and_writer_bytes_for_every_accepted_length()
    {
        foreach (var length in Enumerable.Range(
                     HardwareLabPolicy.LogicalReportLength,
                     HardwareLabPolicy.MaximumWireReportLength - HardwareLabPolicy.LogicalReportLength + 1))
        {
            var target = new LabTargetSnapshot(true, "RC73XA", "identity", length, "approved");
            var operation = HardwareLabPolicy.CreateApprovedOperation(target);
            var writerBytes = operation.CopyWirePacket();

            Assert.Equal(length, operation.WireLength);
            Assert.Equal(operation.WireHex, Convert.ToHexString(writerBytes));
            Assert.Equal(operation.WireSha256, HardwareLabPolicy.Sha256Hex(writerBytes));
            Assert.Equal(HardwareLabPolicy.BuildWirePacket(length), writerBytes);

            writerBytes[0] ^= 0xFF;
            Assert.Equal(HardwareLabPolicy.FeatureReportId, operation.CopyWirePacket()[0]);
        }
    }

    [Fact]
    public void Approved_operation_rejects_any_internally_nonfixed_packet()
    {
        var target = new LabTargetSnapshot(true, "RC73XA", "identity", 50, "approved");
        var operation = new HardwareLabPolicy.ApprovedOperation(target, new byte[50]);

        Assert.Throws<InvalidOperationException>(() => operation.CopyWirePacket());
    }

    [Fact]
    public void Native_caps_layout_finds_only_the_requested_report_id()
    {
        Assert.Equal(72, NativeHidLayout.ValueCapsSize);
        Assert.Equal(72, NativeHidLayout.ButtonCapsSize);
        Assert.Equal(2, NativeHidLayout.ReportIdOffset);
        Assert.Equal(64, NativeHidLayout.HidpCapsSize);
        Assert.Equal(12, NativeHidLayout.HiddAttributesSize);
        Assert.Equal(32, NativeHidLayout.DeviceInterfaceDataSize);
        Assert.Equal(8, NativeHidLayout.DeviceInterfaceDetailCbSize);
        Assert.Equal(4, NativeHidLayout.DeviceInterfacePathOffset);

        var buffer = Marshal.AllocHGlobal(NativeHidLayout.ValueCapsSize * 2);
        try
        {
            for (var index = 0; index < NativeHidLayout.ValueCapsSize * 2; index++)
                Marshal.WriteByte(buffer, index, 0);
            Marshal.WriteByte(buffer, NativeHidLayout.ReportIdOffset, 0x01);
            Marshal.WriteByte(buffer, NativeHidLayout.ValueCapsSize + NativeHidLayout.ReportIdOffset, 0x5A);

            Assert.True(NativeHidLayout.ContainsReportId(buffer, 2, NativeHidLayout.ValueCapsSize, 0x5A));
            Assert.False(NativeHidLayout.ContainsReportId(buffer, 2, NativeHidLayout.ValueCapsSize, 0x5B));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void Native_writer_marshalling_matches_the_pinned_x64_windows_abi()
    {
        var writer = typeof(ExactRc73xaLabWriter);
        var nested = BindingFlags.NonPublic;
        Assert.Equal(NativeHidLayout.HidpCapsSize, Marshal.SizeOf(writer.GetNestedType("HidpCaps", nested)!));
        Assert.Equal(NativeHidLayout.HiddAttributesSize, Marshal.SizeOf(writer.GetNestedType("HiddAttributes", nested)!));
        Assert.Equal(NativeHidLayout.DeviceInterfaceDataSize, Marshal.SizeOf(writer.GetNestedType("SpDeviceInterfaceData", nested)!));

        var methods = writer.GetMethods(BindingFlags.NonPublic | BindingFlags.Static);
        var valueCaps = Assert.Single(methods, method => method.Name == "HidP_GetSpecificValueCaps");
        var buttonCaps = Assert.Single(methods, method => method.Name == "HidP_GetSpecificButtonCaps");
        Assert.Equal(typeof(ushort).MakeByRefType(), valueCaps.GetParameters()[5].ParameterType);
        Assert.Equal(typeof(ushort).MakeByRefType(), buttonCaps.GetParameters()[5].ParameterType);

        var setFeature = Assert.Single(methods, method => method.Name == "HidD_SetFeature");
        Assert.Equal(3, setFeature.GetParameters().Length);
        Assert.Equal(typeof(byte[]), setFeature.GetParameters()[1].ParameterType);
        Assert.Equal(typeof(int), setFeature.GetParameters()[2].ParameterType);
        var import = setFeature.GetCustomAttribute<DllImportAttribute>();
        Assert.NotNull(import);
        Assert.Equal("hid.dll", import.Value);
        Assert.True(import.SetLastError);
    }
}
