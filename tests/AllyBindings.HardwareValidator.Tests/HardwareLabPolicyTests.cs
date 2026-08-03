using System.Security.Cryptography;

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
}
