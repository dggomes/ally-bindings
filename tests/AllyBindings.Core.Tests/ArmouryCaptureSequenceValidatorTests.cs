using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class ArmouryCaptureSequenceValidatorTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Accepts_exactly_one_expected_report_in_each_action_window()
    {
        var result = Validate(ValidReports());

        Assert.True(result.IsConclusive);
        Assert.True(result.FirstMappingMatched);
        Assert.True(result.SecondMappingMatched);
        Assert.True(result.NativeResetMatched);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void Rejects_expected_report_plus_mismatched_extra()
    {
        var reports = ValidReports().Append(Evidence(2.5)).ToArray();

        var result = Validate(reports);

        Assert.False(result.IsConclusive);
        Assert.Contains(result.Reasons, reason => reason.Contains("exactly one", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_duplicate_expected_report()
    {
        var reports = ValidReports().Append(Evidence(2.5, first: true)).ToArray();

        var result = Validate(reports);

        Assert.False(result.IsConclusive);
        Assert.False(result.FirstMappingMatched);
    }

    [Fact]
    public void Rejects_structurally_invalid_report_even_when_vector_flag_matches()
    {
        var reports = ValidReports();
        reports[1] = Evidence(6, valid: false, second: true);

        var result = Validate(reports);

        Assert.False(result.IsConclusive);
        Assert.Contains(result.Reasons, reason => reason.Contains("structurally invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_expected_vector_in_the_wrong_window()
    {
        var reports = ValidReports();
        reports[0] = Evidence(2, second: true);

        var result = Validate(reports);

        Assert.False(result.IsConclusive);
        Assert.False(result.FirstMappingMatched);
        Assert.Contains(result.Reasons, reason => reason.Contains("does not match", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_report_outside_all_action_windows()
    {
        var reports = ValidReports().Append(Evidence(20, reset: true)).ToArray();

        var result = Validate(reports);

        Assert.False(result.IsConclusive);
        Assert.Contains(result.Reasons, reason => reason.Contains("outside every action window", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_overlapping_or_out_of_sequence_action_windows()
    {
        var windows = Windows();
        windows[1] = windows[1] with { Started = Origin.AddSeconds(2.5) };

        var result = ArmouryCaptureSequenceValidator.Validate(
            ValidReports(),
            windows,
            captureFailureCount: 0,
            captureScopeVerified: true,
            targetIdentityStable: true);

        Assert.False(result.IsConclusive);
        Assert.Contains(result.Reasons, reason => reason.Contains("overlap", StringComparison.Ordinal));
    }

    [Fact]
    public void Accepts_abutting_action_windows_without_treating_them_as_overlap()
    {
        var windows = Windows();
        windows[1] = windows[1] with { Started = windows[0].Completed };
        windows[2] = windows[2] with { Started = windows[1].Completed };

        var result = ArmouryCaptureSequenceValidator.Validate(
            ValidReports(),
            windows,
            captureFailureCount: 0,
            captureScopeVerified: true,
            targetIdentityStable: true);

        Assert.True(result.IsConclusive);
    }

    [Fact]
    public void Shared_boundary_belongs_only_to_the_completed_window()
    {
        var windows = Windows();
        windows[1] = windows[1] with { Started = windows[0].Completed };
        var reports = ValidReports();
        reports[1] = Evidence(3, second: true);

        var result = ArmouryCaptureSequenceValidator.Validate(
            reports,
            windows,
            captureFailureCount: 0,
            captureScopeVerified: true,
            targetIdentityStable: true);

        Assert.False(result.IsConclusive);
        Assert.False(result.SecondMappingMatched);
    }

    [Theory]
    [InlineData(1, true, true)]
    [InlineData(0, false, true)]
    [InlineData(0, true, false)]
    public void Rejects_dropped_events_unverified_scope_or_identity_change(
        int captureFailureCount,
        bool captureScopeVerified,
        bool targetStable)
    {
        var result = Validate(ValidReports(), captureFailureCount, captureScopeVerified, targetStable);

        Assert.False(result.IsConclusive);
    }

    private static ArmouryCaptureSequenceValidation Validate(
        IReadOnlyList<ArmouryCaptureReportEvidence> reports,
        int captureFailureCount = 0,
        bool captureScopeVerified = true,
        bool targetStable = true) =>
        ArmouryCaptureSequenceValidator.Validate(
            reports,
            Windows(),
            captureFailureCount,
            captureScopeVerified,
            targetStable);

    private static ArmouryCaptureReportEvidence[] ValidReports() =>
    [
        Evidence(2, first: true),
        Evidence(6, second: true),
        Evidence(10, reset: true),
    ];

    private static ArmouryCaptureStepWindow[] Windows() =>
    [
        new("M1=A / M2=B", Origin.AddSeconds(1), Origin.AddSeconds(3), ArmouryCaptureExpectedReport.M1A_M2B),
        new("M1=X / M2=Y", Origin.AddSeconds(5), Origin.AddSeconds(7), ArmouryCaptureExpectedReport.M1X_M2Y),
        new("Reset to Default", Origin.AddSeconds(9), Origin.AddSeconds(11), ArmouryCaptureExpectedReport.NativeReset),
    ];

    private static ArmouryCaptureReportEvidence Evidence(
        double seconds,
        bool valid = true,
        bool first = false,
        bool second = false,
        bool reset = false) =>
        new(Origin.AddSeconds(seconds), valid, first, second, reset);
}
