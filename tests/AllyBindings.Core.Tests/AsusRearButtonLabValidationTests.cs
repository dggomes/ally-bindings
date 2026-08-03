using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class AsusRearButtonLabValidationTests
{
    [Fact]
    public void One_shot_report_is_fixed_to_m1_a_and_m2_b()
    {
        var expected = Convert.FromHexString(
            "5AD102082C010200000000000000000001020000000000000000000101000000000000000000010100000000000000000000");

        var actual = AsusRearButtonLabValidation.BuildOneShotReport();

        Assert.Equal(expected, actual);
        Assert.Equal(
            "fb0f2ac8167350edf147fb839be2306ccb15494c824a44badeff7aad083cf38b",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(actual)).ToLowerInvariant());
    }

    [Theory]
    [InlineData("", "I SAVED SETTINGS; WRITE M1=A M2=B", false, 1)]
    [InlineData("write-m1-a-m2-b", "wrong", false, 1)]
    [InlineData("write-m1-a-m2-b", "I SAVED SETTINGS; WRITE M1=A M2=B", true, 1)]
    [InlineData("write-m1-a-m2-b", "I SAVED SETTINGS; WRITE M1=A M2=B", false, 0)]
    [InlineData("write-m1-a-m2-b", "I SAVED SETTINGS; WRITE M1=A M2=B", false, 2)]
    public void One_shot_write_rejects_every_incomplete_or_ambiguous_precondition(
        string command,
        string confirmation,
        bool inputRedirected,
        int compatibleInterfaceCount)
    {
        var decision = AsusRearButtonLabValidation.Authorize(
            command,
            confirmation,
            inputRedirected,
            compatibleInterfaceCount);

        Assert.False(decision.Approved);
        Assert.NotEmpty(decision.Message);
    }

    [Fact]
    public void One_shot_write_requires_exact_command_confirmation_and_one_interface()
    {
        var decision = AsusRearButtonLabValidation.Authorize(
            AsusRearButtonLabValidation.WriteCommand,
            AsusRearButtonLabValidation.ConfirmationPhrase,
            inputRedirected: false,
            compatibleInterfaceCount: 1);

        Assert.True(decision.Approved);
    }

    [Fact]
    public void Lab_validation_does_not_unlock_the_application_backend()
    {
        Assert.False(ArmouryProtocolValidation.CustomWritesApproved);
        Assert.False(ArmouryProtocolValidation.RecoveryWritesApproved);
    }

    [Fact]
    public void Interface_snapshot_requires_one_or_more_exact_ordered_identities()
    {
        Assert.False(AsusRearButtonLabValidation.IsExactInterfaceSnapshot([], []));
        Assert.True(AsusRearButtonLabValidation.IsExactInterfaceSnapshot(["A", "B"], ["A", "B"]));
        Assert.False(AsusRearButtonLabValidation.IsExactInterfaceSnapshot(["A", "B"], ["B", "A"]));
        Assert.False(AsusRearButtonLabValidation.IsExactInterfaceSnapshot(["A"], ["A", "B"]));
        Assert.False(AsusRearButtonLabValidation.IsExactInterfaceSnapshot(["A"], ["a"]));
    }
}
