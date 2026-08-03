namespace AllyBindings.Core.Tests;

public sealed class ArmouryTapProtocolTests
{
    [Fact]
    public void Candidate_process_names_include_current_armoury_components()
    {
        var expected = new[]
        {
            "ArmouryCrateSE.Service",
            "ArmouryCrate.Service",
            "ArmouryCrateSE",
            "ArmouryCrate.UserSessionHelper",
            "ArmouryCrateControlInterface",
            "ArmourySocketServer",
            "ArmourySwAgent",
            "ArmouryCrateKeyControl",
            "AsusOptimization",
        };

        Assert.Equal(expected, ArmouryTapProtocol.ExactCandidateProcessNames);
    }

    [Fact]
    public void IsExactCandidateProcessName_is_case_insensitive()
    {
        Assert.True(ArmouryTapProtocol.IsExactCandidateProcessName("armourycratese.service"));
        Assert.True(ArmouryTapProtocol.IsExactCandidateProcessName("ARMOURYCRATE.USERSESSIONHELPER"));
        Assert.True(ArmouryTapProtocol.IsExactCandidateProcessName("ASUSOPTIMIZATION"));
    }

    [Fact]
    public void IsExactCandidateProcessName_rejects_unknown_names()
    {
        Assert.False(ArmouryTapProtocol.IsExactCandidateProcessName("explorer"));
        Assert.False(ArmouryTapProtocol.IsExactCandidateProcessName(null));
        Assert.False(ArmouryTapProtocol.IsExactCandidateProcessName(""));
    }

    [Fact]
    public void IsSupportedDevice_requires_exact_asus_ally_ids()
    {
        Assert.True(ArmouryTapProtocol.IsSupportedDevice(0x0B05, 0x1B4C));
        Assert.False(ArmouryTapProtocol.IsSupportedDevice(0x0B05, 0x1B6E));
        Assert.False(ArmouryTapProtocol.IsSupportedDevice(0x045E, 0x1B4C));
    }

    [Fact]
    public void IsRetainableReport_requires_rear_mapping_command_and_length_bounds()
    {
        var valid = new byte[64];
        valid[0] = 0x5A;
        valid[1] = 0xD1;
        Assert.True(ArmouryTapProtocol.IsRetainableReport(valid));

        var minValid = new byte[50];
        minValid[0] = 0x5A;
        minValid[1] = 0xD1;
        Assert.True(ArmouryTapProtocol.IsRetainableReport(minValid));

        var wrongId = new byte[64];
        wrongId[0] = 0x5B;
        wrongId[1] = 0xD1;
        Assert.False(ArmouryTapProtocol.IsRetainableReport(wrongId));

        var wrongCommand = new byte[64];
        wrongCommand[0] = 0x5A;
        wrongCommand[1] = 0x99;
        Assert.False(ArmouryTapProtocol.IsRetainableReport(wrongCommand));

        var tooShort = new byte[49];
        tooShort[0] = 0x5A;
        tooShort[1] = 0xD1;
        Assert.False(ArmouryTapProtocol.IsRetainableReport(tooShort));

        var tooLong = new byte[65];
        tooLong[0] = 0x5A;
        tooLong[1] = 0xD1;
        Assert.False(ArmouryTapProtocol.IsRetainableReport(tooLong));
    }

    [Fact]
    public void Wire_constants_are_stable()
    {
        Assert.Equal(0x31544241u, ArmouryTapProtocol.WireMagic);
        Assert.Equal(1, ArmouryTapProtocol.WireVersion);
        Assert.Equal(124, ArmouryTapProtocol.WireRecordSize);
        Assert.Equal(256, ArmouryTapProtocol.MaximumRecords);
    }

    [Fact]
    public void Startup_deadline_covers_every_bounded_candidate_lifecycle()
    {
        var perCandidateLifecycleBudget =
            ArmouryTapProtocol.CandidateHandshakeStepTimeout * 2 +
            ArmouryTapProtocol.CandidateRemoteCallTimeout * 3;
        var allCandidatesLifecycleBudget =
            ArmouryTapProtocol.MaximumCandidateProcesses * perCandidateLifecycleBudget;

        Assert.Equal(perCandidateLifecycleBudget, ArmouryTapProtocol.CandidateWorstCaseStartupDuration);
        Assert.True(
            ArmouryTapProtocol.CaptureStartupTimeout >= allCandidatesLifecycleBudget + TimeSpan.FromSeconds(60));
    }
}
