using System.Collections.Immutable;
using System.Security.Cryptography;
using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class AsusFeatureReportSnapshotTests
{
    private const string DeviceId = "VID_0B05&PID_1B6E:report_5A";

    [Fact]
    public void Matching_four_stage_sequence_is_classified_but_never_unlock_evidence()
    {
        var baseline = Pad(AsusRearButtonProtocol.BuildNativeResetReport());
        var first = Pad(AsusRearButtonProtocol.BuildMappingReport(ControllerButton.A, ControllerButton.B));
        var second = Pad(AsusRearButtonProtocol.BuildMappingReport(ControllerButton.X, ControllerButton.Y));
        var reset = baseline.ToArray();

        var result = AsusFeatureReportSnapshotAnalyzer.Analyze(Captures(baseline, first, second, reset));

        Assert.True(result.DiagnosticOnly);
        Assert.False(result.HardwareUnlockEvidence);
        Assert.True(result.AllStagesReadable);
        Assert.True(result.CandidateSequenceMatched);
        Assert.True(result.ResetReturnedToBaseline);
        Assert.All(result.Reads, read => Assert.True(read.LengthBounded));
        Assert.All(result.Reads, read => Assert.True(read.HashValid));
        Assert.Equal(3, result.Reads.Count(read => read.MatchesExpectedWireReport == true));
        Assert.Contains(result.Reasons, reason => reason.Contains("zero hardware-write authority", StringComparison.Ordinal));
    }

    [Fact]
    public void Diff_reports_changed_offsets_and_reset_baseline_equality()
    {
        var baseline = Pad(AsusRearButtonProtocol.BuildNativeResetReport());
        var first = baseline.ToArray();
        first[10] ^= 0x7F;
        var second = first.ToArray();
        second[11] ^= 0x55;

        var result = AsusFeatureReportSnapshotAnalyzer.Analyze(Captures(baseline, first, second, baseline));

        var firstDiff = Assert.Single(result.Diffs, diff =>
            diff.FromStage == "Baseline" && diff.ToStage == "M1A_M2B");
        Assert.True(firstDiff.Comparable);
        Assert.Equal([10], firstDiff.ChangedOffsets);
        Assert.True(result.ResetReturnedToBaseline);
        Assert.False(result.CandidateSequenceMatched);
    }

    [Fact]
    public void Constant_non_mapping_report_is_legible_but_inconclusive()
    {
        var report = Enumerable.Repeat((byte)0xA5, 64).ToArray();

        var result = AsusFeatureReportSnapshotAnalyzer.Analyze(Captures(report, report, report, report));

        Assert.True(result.AllStagesReadable);
        Assert.False(result.CandidateSequenceMatched);
        Assert.True(result.ResetReturnedToBaseline);
        Assert.All(result.Reads, read =>
        {
            Assert.False(read.HasExpectedReportId);
            Assert.False(read.HasRearMappingPrefix);
        });
    }

    [Theory]
    [InlineData(49)]
    [InlineData(65)]
    public void Out_of_bounds_reports_are_not_comparable(int length)
    {
        var invalid = new byte[length];
        invalid[0] = 0x5A;

        var result = AsusFeatureReportSnapshotAnalyzer.Analyze(Captures(invalid, invalid, invalid, invalid));

        Assert.False(result.AllStagesReadable);
        Assert.Null(result.ResetReturnedToBaseline);
        Assert.All(result.Reads, read => Assert.False(read.LengthBounded));
        Assert.All(result.Diffs, diff => Assert.False(diff.Comparable));
    }

    [Fact]
    public void Empty_unreadable_stage_fails_closed()
    {
        var readable = Pad(AsusRearButtonProtocol.BuildNativeResetReport());
        var captures = Captures(readable, readable, readable, readable).ToArray();
        captures[1] = new(
            AsusFeatureReportSnapshotStage.M1A_M2B,
            new(Attempted: true, Succeeded: false, Reads: [new(DeviceId, [], string.Empty, "Failed")], Message: "Failed"));

        var result = AsusFeatureReportSnapshotAnalyzer.Analyze(captures);

        Assert.False(result.AllStagesReadable);
        Assert.False(result.CandidateSequenceMatched);
        Assert.Contains(result.Reasons, reason => reason.Contains("no bounded successful", StringComparison.Ordinal));
    }

    [Fact]
    public void Hash_mismatch_is_detected_without_changing_report_analysis()
    {
        var report = Pad(AsusRearButtonProtocol.BuildNativeResetReport());
        var captures = Captures(report, report, report, report).ToArray();
        captures[0] = new(
            AsusFeatureReportSnapshotStage.Baseline,
            new(true, true, [new(DeviceId, ImmutableArray.CreateRange(report), new string('0', 64), "Read")], "Read"));

        var result = AsusFeatureReportSnapshotAnalyzer.Analyze(captures);

        Assert.False(result.Reads.Single(read => read.Stage == "Baseline").HashValid);
        Assert.False(result.HardwareUnlockEvidence);
    }

    [Fact]
    public void Missing_or_reordered_stage_is_rejected()
    {
        var report = Pad(AsusRearButtonProtocol.BuildNativeResetReport());
        var captures = Captures(report, report, report, report).Reverse().ToArray();

        Assert.Throws<ArgumentException>(() => AsusFeatureReportSnapshotAnalyzer.Analyze(captures));
    }

    [Fact]
    public void Candidate_sequence_must_match_on_one_common_interface()
    {
        const string secondDevice = "VID_0B05&PID_1ABE:report_5A";
        var baseline = Pad(AsusRearButtonProtocol.BuildNativeResetReport());
        var first = Pad(AsusRearButtonProtocol.BuildMappingReport(ControllerButton.A, ControllerButton.B));
        var second = Pad(AsusRearButtonProtocol.BuildMappingReport(ControllerButton.X, ControllerButton.Y));
        var unrelated = Enumerable.Repeat((byte)0x44, 64).ToArray();
        AsusRearButtonReadResult Stage(params (string Device, byte[] Report)[] reports) => new(
            true,
            true,
            reports.Select(item => new AsusFeatureReportRead(
                item.Device,
                ImmutableArray.CreateRange(item.Report),
                Hash(item.Report),
                "Read")).ToArray(),
            "Read");
        var captures = new AsusFeatureReportSnapshotCapture[]
        {
            new(AsusFeatureReportSnapshotStage.Baseline, Stage((DeviceId, baseline), (secondDevice, baseline))),
            new(AsusFeatureReportSnapshotStage.M1A_M2B, Stage((DeviceId, first), (secondDevice, unrelated))),
            new(AsusFeatureReportSnapshotStage.M1X_M2Y, Stage((DeviceId, unrelated), (secondDevice, second))),
            new(AsusFeatureReportSnapshotStage.ResetToDefault, Stage((DeviceId, baseline), (secondDevice, baseline))),
        };

        var result = AsusFeatureReportSnapshotAnalyzer.Analyze(captures);

        Assert.False(result.CandidateSequenceMatched);
        Assert.False(result.HardwareUnlockEvidence);
    }

    private static IReadOnlyList<AsusFeatureReportSnapshotCapture> Captures(
        byte[] baseline,
        byte[] first,
        byte[] second,
        byte[] reset) =>
    [
        new(AsusFeatureReportSnapshotStage.Baseline, Result(baseline)),
        new(AsusFeatureReportSnapshotStage.M1A_M2B, Result(first)),
        new(AsusFeatureReportSnapshotStage.M1X_M2Y, Result(second)),
        new(AsusFeatureReportSnapshotStage.ResetToDefault, Result(reset)),
    ];

    private static AsusRearButtonReadResult Result(byte[] report) => new(
        Attempted: true,
        Succeeded: true,
        Reads: [new(DeviceId, ImmutableArray.CreateRange(report), Hash(report), "Read")],
        Message: "Read");

    private static byte[] Pad(byte[] report)
    {
        var padded = new byte[64];
        report.CopyTo(padded, 0);
        return padded;
    }

    private static string Hash(byte[] report) =>
        Convert.ToHexString(SHA256.HashData(report)).ToLowerInvariant();
}
