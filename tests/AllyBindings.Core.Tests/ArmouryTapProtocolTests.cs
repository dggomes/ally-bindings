using System.Buffers.Binary;

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
        Assert.Equal(2, ArmouryTapProtocol.WireVersion);
        Assert.Equal(124, ArmouryTapProtocol.WireRecordSize);
        Assert.Equal(256, ArmouryTapProtocol.MaximumRecords);
        Assert.Equal(0xFE, ArmouryTapProtocol.SummaryRecordApi);
        Assert.Equal(0xFF, ArmouryTapProtocol.OverflowRecordApi);
    }

    [Fact]
    public void Expanded_api_ids_are_distinct_and_bounded()
    {
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 },
            Enum.GetValues<ArmouryTapApi>().Select(value => (byte)value));
        Assert.All(Enum.GetValues<ArmouryTapApi>(), api =>
            Assert.True((byte)api < ArmouryTapProtocol.SummaryRecordApi));
    }

    [Fact]
    public void Diagnostics_schema_contains_aggregate_counts_but_no_raw_identity_or_payload_fields()
    {
        var properties = typeof(ArmouryTapPreFilterDiagnostics).GetProperties()
            .Select(property => property.Name).ToArray();
        Assert.Contains("ProcessName", properties);
        Assert.Contains("DeviceIoControlSetOutputReportCallCount", properties);
        Assert.Contains("AttributeReadFailureCount", properties);
        Assert.Contains("CounterSaturated", properties);
        Assert.DoesNotContain(properties, name =>
            name.Contains("Pid", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Handle", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("RawHandle", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Timestamp", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Payload", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Report", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ReportBytes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Diagnostic_summary_decodes_a_monotonic_target_only_funnel()
    {
        var raw = BuildSummary(0, 0, 0, 0, 0, 0, 3, 0, 0, 2, 1, 1, 0);
        var result = ArmouryTapProtocol.DecodeDiagnosticSummary(
            "ArmouryCrateSE.Service", 3, 0, 0, raw, 1);

        Assert.Equal(3u, result.HidDSetFeatureCallCount);
        Assert.Equal(2u, result.ReportId5ACount);
        Assert.Equal(1u, result.Prefix5AD1Count);
        Assert.Equal(1u, result.RetainedRecordCount);
        Assert.False(result.CounterSaturated);
    }

    [Fact]
    public void Diagnostic_summary_rejects_nonmonotonic_or_nonzero_reserved_data()
    {
        var nonmonotonic = BuildSummary(0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 1, 0);
        Assert.Throws<InvalidDataException>(() =>
            ArmouryTapProtocol.DecodeDiagnosticSummary("ArmouryCrateSE.Service", 1, 0, 0, nonmonotonic, 1));

        var reserved = BuildSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        reserved[63] = 1;
        Assert.Throws<InvalidDataException>(() =>
            ArmouryTapProtocol.DecodeDiagnosticSummary("ArmouryCrateSE.Service", 0, 0, 0, reserved, 0));

        var saturated = BuildSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        saturated[1] = 1;
        var saturatedResult = ArmouryTapProtocol.DecodeDiagnosticSummary(
            "ArmouryCrateSE.Service", ArmouryTapProtocol.MaximumDiagnosticCounter, 0, 0, saturated, 0);
        Assert.True(saturatedResult.CounterSaturated);
    }

    private static byte[] BuildSummary(params uint[] counters)
    {
        Assert.Equal(13, counters.Length);
        var raw = new byte[64];
        raw[0] = ArmouryTapProtocol.SummarySchemaVersion;
        for (var index = 0; index < counters.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4 + index * 4), counters[index]);
        return raw;
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
