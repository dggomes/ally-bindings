namespace AllyBindings.Core;

internal enum ArmouryCaptureExpectedReport
{
    M1A_M2B,
    M1X_M2Y,
    NativeReset,
}

internal sealed record ArmouryCaptureReportEvidence(
    DateTimeOffset Timestamp,
    bool IsStructurallyValid,
    bool MatchesM1A_M2B,
    bool MatchesM1X_M2Y,
    bool MatchesNativeReset);

internal sealed record ArmouryCaptureStepWindow(
    string Label,
    DateTimeOffset Started,
    DateTimeOffset Completed,
    ArmouryCaptureExpectedReport ExpectedReport);

internal sealed record ArmouryCaptureSequenceValidation(
    bool IsConclusive,
    bool FirstMappingMatched,
    bool SecondMappingMatched,
    bool NativeResetMatched,
    IReadOnlyList<string> Reasons);

internal static class ArmouryCaptureSequenceValidator
{
    public static ArmouryCaptureSequenceValidation Validate(
        IReadOnlyList<ArmouryCaptureReportEvidence> reports,
        IReadOnlyList<ArmouryCaptureStepWindow> windows,
        int captureFailureCount,
        bool captureScopeVerified,
        bool targetIdentityStable,
        bool schemaDiscoveryIncomplete = false)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(windows);
        var reasons = new List<string>();
        var matched = new Dictionary<ArmouryCaptureExpectedReport, bool>();

        if (windows.Count != 3)
        {
            reasons.Add($"Expected three action windows, but found {windows.Count}.");
        }
        for (var index = 1; index < windows.Count; index++)
        {
            if (windows[index].Started < windows[index - 1].Completed)
            {
                reasons.Add("The action windows overlap or are out of sequence.");
            }
        }

        foreach (var window in windows)
        {
            if (window.Completed < window.Started)
            {
                reasons.Add($"The {window.Label} action markers are out of order.");
                matched[window.ExpectedReport] = false;
                continue;
            }

            var reportsInWindow = reports
                .Where(report => report.Timestamp > window.Started && report.Timestamp <= window.Completed)
                .ToList();
            if (reportsInWindow.Count != 1)
            {
                reasons.Add(
                    $"The {window.Label} action window contains {reportsInWindow.Count} ASUS report 0x5A packets; exactly one is required.");
                matched[window.ExpectedReport] = false;
                continue;
            }

            var report = reportsInWindow[0];
            if (!report.IsStructurallyValid)
            {
                reasons.Add($"The report in the {window.Label} action window is structurally invalid.");
            }
            if (!MatchesExpected(report, window.ExpectedReport))
            {
                reasons.Add($"The report in the {window.Label} action window does not match the requested exact vector.");
            }
            matched[window.ExpectedReport] = report.IsStructurallyValid && MatchesExpected(report, window.ExpectedReport);
        }

        foreach (var report in reports)
        {
            var containingWindowCount = windows.Count(window =>
                report.Timestamp > window.Started && report.Timestamp <= window.Completed);
            if (containingWindowCount == 0)
            {
                reasons.Add("An unexplained ASUS report 0x5A packet occurred outside every action window.");
            }
            else if (containingWindowCount > 1)
            {
                reasons.Add("An ASUS report 0x5A packet falls into overlapping action windows.");
            }
        }

        if (captureFailureCount != 0)
        {
            reasons.Add($"The ETW collector recorded {captureFailureCount} dropped, oversized or undecodable event(s).");
        }
        if (schemaDiscoveryIncomplete)
        {
            reasons.Add("The metadata-only ETW schema inventory reached its retention limit and is incomplete.");
        }
        if (!captureScopeVerified)
        {
            reasons.Add("The capture scope could not be verified as the integrated filtered USB ETW session.");
        }
        if (!targetIdentityStable)
        {
            reasons.Add("The selected ASUS USB identity changed or disappeared before post-capture verification.");
        }

        var first = matched.GetValueOrDefault(ArmouryCaptureExpectedReport.M1A_M2B);
        var second = matched.GetValueOrDefault(ArmouryCaptureExpectedReport.M1X_M2Y);
        var reset = matched.GetValueOrDefault(ArmouryCaptureExpectedReport.NativeReset);
        return new(reasons.Count == 0 && first && second && reset, first, second, reset, reasons);
    }

    private static bool MatchesExpected(
        ArmouryCaptureReportEvidence report,
        ArmouryCaptureExpectedReport expected) => expected switch
        {
            ArmouryCaptureExpectedReport.M1A_M2B => report.MatchesM1A_M2B,
            ArmouryCaptureExpectedReport.M1X_M2Y => report.MatchesM1X_M2Y,
            ArmouryCaptureExpectedReport.NativeReset => report.MatchesNativeReset,
            _ => false,
        };
}
