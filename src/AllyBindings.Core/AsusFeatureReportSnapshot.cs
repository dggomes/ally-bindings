using System.Security.Cryptography;

namespace AllyBindings.Core;

public enum AsusFeatureReportSnapshotStage
{
    Baseline,
    M1A_M2B,
    M1X_M2Y,
    ResetToDefault,
}

public sealed record AsusFeatureReportSnapshotCapture(
    AsusFeatureReportSnapshotStage Stage,
    AsusRearButtonReadResult Result);

public sealed record AsusFeatureReportSnapshotReadAnalysis(
    string Stage,
    string DeviceId,
    int InterfaceOrdinal,
    int ReportLength,
    bool LengthBounded,
    bool HashValid,
    bool HasExpectedReportId,
    bool HasRearMappingPrefix,
    bool? MatchesExpectedWireReport);

public sealed record AsusFeatureReportSnapshotDiff(
    string DeviceId,
    int InterfaceOrdinal,
    string FromStage,
    string ToStage,
    bool Comparable,
    bool Equal,
    IReadOnlyList<int> ChangedOffsets);

public sealed record AsusFeatureReportSnapshotAnalysis(
    bool DiagnosticOnly,
    bool HardwareUnlockEvidence,
    bool AllStagesReadable,
    bool CandidateSequenceMatched,
    bool? ResetReturnedToBaseline,
    IReadOnlyList<AsusFeatureReportSnapshotReadAnalysis> Reads,
    IReadOnlyList<AsusFeatureReportSnapshotDiff> Diffs,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Pure analysis for four target-scoped report-0x5A readback stages. Results are
/// diagnostic-only and cannot change either hardware write gate.
/// </summary>
public static class AsusFeatureReportSnapshotAnalyzer
{
    private static ReadOnlySpan<byte> RearMappingPrefix => [0x5A, 0xD1, 0x02, 0x08, 0x2C];
    private static readonly AsusFeatureReportSnapshotStage[] RequiredStages =
    [
        AsusFeatureReportSnapshotStage.Baseline,
        AsusFeatureReportSnapshotStage.M1A_M2B,
        AsusFeatureReportSnapshotStage.M1X_M2Y,
        AsusFeatureReportSnapshotStage.ResetToDefault,
    ];

    public static AsusFeatureReportSnapshotAnalysis Analyze(
        IReadOnlyList<AsusFeatureReportSnapshotCapture> captures)
    {
        ArgumentNullException.ThrowIfNull(captures);
        if (captures.Count != RequiredStages.Length ||
            !captures.Select(capture => capture.Stage).SequenceEqual(RequiredStages))
        {
            throw new ArgumentException(
                "Snapshots must contain baseline, A/B, X/Y and reset exactly once in order.",
                nameof(captures));
        }
        if (captures.Any(capture => capture.Result is null))
        {
            throw new ArgumentException("Every snapshot stage requires a read result.", nameof(captures));
        }

        var analyses = new List<AsusFeatureReportSnapshotReadAnalysis>();
        var indexedReports = new Dictionary<AsusFeatureReportSnapshotStage, Dictionary<InterfaceKey, byte[]>>();
        foreach (var capture in captures)
        {
            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var stageReports = new Dictionary<InterfaceKey, byte[]>();
            foreach (var read in capture.Result.Reads)
            {
                ArgumentNullException.ThrowIfNull(read);
                var ordinal = ordinals.GetValueOrDefault(read.DeviceId) + 1;
                ordinals[read.DeviceId] = ordinal;
                var key = new InterfaceKey(read.DeviceId, ordinal);
                var report = read.Report.ToArray();
                stageReports[key] = report;

                var bounded = IsBounded(report);
                var expected = ExpectedReport(capture.Stage);
                analyses.Add(new(
                    capture.Stage.ToString(),
                    read.DeviceId,
                    ordinal,
                    report.Length,
                    bounded,
                    HashValid(report, read.Sha256),
                    report.Length > 0 && report[0] == AsusRearButtonProtocol.FeatureReportId,
                    report.AsSpan().StartsWith(RearMappingPrefix),
                    expected is null ? null : bounded && AsusRearButtonProtocol.MatchesWireReport(report, expected)));
            }
            indexedReports[capture.Stage] = stageReports;
        }

        var diffs = new List<AsusFeatureReportSnapshotDiff>();
        AddDiffs(indexedReports, AsusFeatureReportSnapshotStage.Baseline, AsusFeatureReportSnapshotStage.M1A_M2B, diffs);
        AddDiffs(indexedReports, AsusFeatureReportSnapshotStage.M1A_M2B, AsusFeatureReportSnapshotStage.M1X_M2Y, diffs);
        AddDiffs(indexedReports, AsusFeatureReportSnapshotStage.M1X_M2Y, AsusFeatureReportSnapshotStage.ResetToDefault, diffs);
        AddDiffs(indexedReports, AsusFeatureReportSnapshotStage.Baseline, AsusFeatureReportSnapshotStage.ResetToDefault, diffs);

        var resetDiffs = diffs
            .Where(diff => diff.FromStage == AsusFeatureReportSnapshotStage.Baseline.ToString() &&
                           diff.ToStage == AsusFeatureReportSnapshotStage.ResetToDefault.ToString())
            .ToArray();
        bool? resetReturnedToBaseline = resetDiffs.Any(diff => diff.Comparable)
            ? resetDiffs.Where(diff => diff.Comparable).All(diff => diff.Equal)
            : null;
        var allStagesReadable = captures.All(capture =>
            capture.Result.Succeeded && capture.Result.Reads.Any(read =>
                read.Report.Length is >= AsusRearButtonProtocol.ReportLength and <= UsbEtwHidFeatureReportExtractor.MaximumWireReportLength));
        var commonInterfaces = RequiredStages
            .Select(stage => indexedReports[stage].Keys.AsEnumerable())
            .Aggregate((left, right) => left.Intersect(right))
            .ToArray();
        var candidateSequenceMatched = commonInterfaces.Any(key =>
            RequiredStages.Skip(1).All(stage =>
            {
                var report = indexedReports[stage][key];
                var expected = ExpectedReport(stage)!;
                return IsBounded(report) && AsusRearButtonProtocol.MatchesWireReport(report, expected);
            }));

        var reasons = new List<string>();
        if (!allStagesReadable) reasons.Add("One or more required snapshot stages had no bounded successful report read.");
        if (!candidateSequenceMatched) reasons.Add("The readbacks did not match all three clean-room A/B, X/Y and reset vectors.");
        if (resetReturnedToBaseline is null) reasons.Add("Baseline and reset could not be compared on a common readable interface.");
        else if (resetReturnedToBaseline == false) reasons.Add("Reset did not return every comparable interface to its baseline bytes.");
        reasons.Add("Readback analysis is review-required diagnostic evidence and has zero hardware-write authority.");

        return new(
            DiagnosticOnly: true,
            HardwareUnlockEvidence: false,
            allStagesReadable,
            candidateSequenceMatched,
            resetReturnedToBaseline,
            analyses,
            diffs,
            reasons);
    }

    private static byte[]? ExpectedReport(AsusFeatureReportSnapshotStage stage) => stage switch
    {
        AsusFeatureReportSnapshotStage.M1A_M2B =>
            AsusRearButtonProtocol.BuildMappingReport(ControllerButton.A, ControllerButton.B),
        AsusFeatureReportSnapshotStage.M1X_M2Y =>
            AsusRearButtonProtocol.BuildMappingReport(ControllerButton.X, ControllerButton.Y),
        AsusFeatureReportSnapshotStage.ResetToDefault => AsusRearButtonProtocol.BuildNativeResetReport(),
        _ => null,
    };

    private static bool IsBounded(byte[]? report) =>
        report is not null &&
        report.Length is >= AsusRearButtonProtocol.ReportLength and <= UsbEtwHidFeatureReportExtractor.MaximumWireReportLength;

    private static bool HashValid(byte[] report, string? expectedHash)
    {
        if (report.Length == 0) return string.IsNullOrEmpty(expectedHash);
        var actual = Convert.ToHexString(SHA256.HashData(report)).ToLowerInvariant();
        return actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddDiffs(
        IReadOnlyDictionary<AsusFeatureReportSnapshotStage, Dictionary<InterfaceKey, byte[]>> reports,
        AsusFeatureReportSnapshotStage fromStage,
        AsusFeatureReportSnapshotStage toStage,
        ICollection<AsusFeatureReportSnapshotDiff> output)
    {
        var from = reports[fromStage];
        var to = reports[toStage];
        var keys = from.Keys.Concat(to.Keys).Distinct().OrderBy(key => key.DeviceId, StringComparer.OrdinalIgnoreCase).ThenBy(key => key.Ordinal);
        foreach (var key in keys)
        {
            from.TryGetValue(key, out var before);
            to.TryGetValue(key, out var after);
            var comparable = IsBounded(before) && IsBounded(after);
            var changed = comparable ? ChangedOffsets(before!, after!) : [];
            output.Add(new(
                key.DeviceId,
                key.Ordinal,
                fromStage.ToString(),
                toStage.ToString(),
                comparable,
                comparable && changed.Count == 0,
                changed));
        }
    }

    private static IReadOnlyList<int> ChangedOffsets(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        var changed = new List<int>();
        var length = Math.Max(before.Length, after.Length);
        for (var index = 0; index < length; index++)
        {
            if (index >= before.Length || index >= after.Length || before[index] != after[index]) changed.Add(index);
        }
        return changed;
    }

    private sealed record InterfaceKey(string DeviceId, int Ordinal);
}
