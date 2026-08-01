using System.Security.Cryptography;

namespace AllyBindings.Core;

/// <summary>
/// Extracts narrow ASUS rear-button report candidates only from individual
/// binary properties decoded by ETW metadata. It never scans a complete raw
/// event payload, where property and transfer boundaries would be ambiguous.
/// Candidates remain diagnostic-only until the target Windows build's USB event
/// schema and device correlation have been validated on physical hardware.
/// </summary>
public static class UsbEtwHidFeatureReportExtractor
{
    public const int MaximumWireReportLength = 64;
    private static ReadOnlySpan<byte> RearMappingPrefix => [0x5A, 0xD1, 0x02, 0x08, 0x2C];

    public static UsbEtwExtractionResult Extract(
        DateTimeOffset timestamp,
        string providerName,
        string eventName,
        int eventId,
        IReadOnlyList<UsbEtwBinaryField> binaryFields,
        int maximumReports)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(binaryFields);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReports);
        eventName ??= string.Empty;

        var reports = new List<UsbEtwFeatureReport>(Math.Min(maximumReports, binaryFields.Count));
        var ambiguousCandidateCount = 0;
        foreach (var field in binaryFields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) ||
                field.Value.Length < AsusRearButtonProtocol.ReportLength)
            {
                continue;
            }

            var searchOffset = 0;
            while (searchOffset <= field.Value.Length - RearMappingPrefix.Length)
            {
                var relativeOffset = field.Value.AsSpan(searchOffset).IndexOf(RearMappingPrefix);
                if (relativeOffset < 0) break;
                var offset = searchOffset + relativeOffset;
                var available = field.Value.Length - offset;
                searchOffset = offset + RearMappingPrefix.Length;
                if (available < AsusRearButtonProtocol.ReportLength || available > MaximumWireReportLength)
                {
                    ambiguousCandidateCount++;
                    continue;
                }
                if (reports.Count == maximumReports)
                {
                    return new(reports, LimitExceeded: true, ambiguousCandidateCount);
                }

                var report = field.Value.AsSpan(offset, available).ToArray();
                reports.Add(new(
                    timestamp,
                    providerName,
                    eventName,
                    eventId,
                    field.Name,
                    offset,
                    report,
                    Convert.ToHexString(SHA256.HashData(report)).ToLowerInvariant()));
            }
        }
        return new(reports, LimitExceeded: false, ambiguousCandidateCount);
    }
}

public sealed record UsbEtwExtractionResult(
    IReadOnlyList<UsbEtwFeatureReport> Reports,
    bool LimitExceeded,
    int AmbiguousCandidateCount);

public sealed record UsbEtwBinaryField(string Name, byte[] Value);

public sealed record UsbEtwFeatureReport(
    DateTimeOffset Timestamp,
    string ProviderName,
    string EventName,
    int EventId,
    string SourceField,
    int SourceOffset,
    byte[] Report,
    string Sha256);
