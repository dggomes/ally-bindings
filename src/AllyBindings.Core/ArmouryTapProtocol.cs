using System.Buffers.Binary;

namespace AllyBindings.Core;

public static class ArmouryTapProtocol
{
    public const ushort AsusVendorId = 0x0B05;
    public const ushort AllyProductId = 0x1B4C;
    public const byte ReportId = 0x5A;
    public const byte RearMappingCommand = 0xD1;
    public const int MinimumReportLength = 50;
    public const int MaximumReportLength = 64;
    public const int MaximumRecords = 256;
    public const int MaximumCandidateProcesses = 12;
    public const int WireRecordSize = 124;
    public const uint WireMagic = 0x31544241; // "ABT1"
    public const ushort WireVersion = 2;
    public const byte SummaryRecordApi = 0xFE;

    public const byte SummarySchemaVersion = 1;
    public const uint MaximumDiagnosticCounter = 1_000_000;
    public const uint CandidateRemoteCallTimeoutMilliseconds = 5_000;
    public static readonly TimeSpan CandidateHandshakeStepTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan CandidateRemoteCallTimeout =
        TimeSpan.FromMilliseconds(CandidateRemoteCallTimeoutMilliseconds);
    public static readonly TimeSpan CandidateWorstCaseStartupDuration =
        CandidateHandshakeStepTimeout * 2 + CandidateRemoteCallTimeout * 3;
    public static readonly TimeSpan CaptureStartupTimeout =
        CandidateWorstCaseStartupDuration * MaximumCandidateProcesses + TimeSpan.FromSeconds(60);

    private static readonly string[] CandidateNames =
    [
        "ArmouryCrateSE.Service",
        "ArmouryCrate.Service",
        "ArmouryCrateSE",
        "ArmouryCrate.UserSessionHelper",
        "ArmouryCrateControlInterface",
        "ArmourySocketServer",
        "ArmourySwAgent",
        "ArmouryCrateKeyControl",
        "AsusOptimization",
    ];

    public static IReadOnlyList<string> ExactCandidateProcessNames => CandidateNames;

    public static bool IsExactCandidateProcessName(string? processName) =>
        processName is not null && CandidateNames.Contains(processName, StringComparer.OrdinalIgnoreCase);

    public static bool IsRetainableReport(ReadOnlySpan<byte> report) =>
        report.Length is >= MinimumReportLength and <= MaximumReportLength &&
        report[0] == ReportId && report[1] == RearMappingCommand;

    public static ArmouryTapPreFilterDiagnostics DecodeDiagnosticSummary(
        string processName,
        long packedApiCallCounts,
        uint setOutputReportCallCount,
        int deviceIoControlSetFeatureCallCount,
        ReadOnlySpan<byte> raw,
        int retainedRecordCount)
    {
        if (raw.Length != 64 || raw[0] != SummarySchemaVersion || raw[1] > 1 ||
            raw[2] != 0 || raw[3] != 0)
            throw new InvalidDataException("Tap diagnostic summary schema was invalid.");
        var values = new uint[13];
        for (var index = 0; index < values.Length; index++)
            values[index] = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(4 + index * 4));
        var packed = unchecked((ulong)packedApiCallCounts);
        var apiCounts = new[]
        {
            unchecked((uint)packed),
            unchecked((uint)(packed >> 32)),
            setOutputReportCallCount,
            unchecked((uint)deviceIoControlSetFeatureCallCount),
            values[0],
        };
        if (raw[56..].ContainsAnyExcept((byte)0) || apiCounts.Concat(values).Any(value => value > MaximumDiagnosticCounter))
            throw new InvalidDataException("Tap diagnostic counters exceeded their bounded schema.");
        var allCalls = apiCounts.Aggregate(0UL, (sum, value) => sum + value);
        var lengthClassifiedCalls = (ulong)values[5] + values[6] + values[7];
        var handleValidationFailures = (ulong)values[1] + values[2] + values[3] + values[4];
        var counterSaturated = raw[1] == 1;
        if (!counterSaturated)
        {
            if (allCalls != lengthClassifiedCalls || values[8] > values[6] ||
                handleValidationFailures > values[9])
                throw new InvalidDataException("Tap diagnostic counters violated their monotonic contract.");
            var readableBoundedCalls = (ulong)values[6] - values[8];
            var targetReportIdCalls = (ulong)values[9] - handleValidationFailures;
            if (values[9] > readableBoundedCalls || values[10] > targetReportIdCalls ||
                values[11] > values[10] || (ulong)retainedRecordCount + values[12] != values[11])
                throw new InvalidDataException("Tap diagnostic counters violated their monotonic contract.");
        }
        return new(processName, apiCounts[0], apiCounts[1], apiCounts[2], apiCounts[3], apiCounts[4],
            values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8],
            values[9], values[10], values[11], values[12], counterSaturated);
    }

    public static bool IsSupportedDevice(ushort vendorId, ushort productId) =>
        vendorId == AsusVendorId && productId == AllyProductId;
}

public enum ArmouryTapApi : byte
{
    HidDSetFeature = 1,
    KernelBaseWriteFile = 2,
    HidDSetOutputReport = 3,
    DeviceIoControlSetFeature = 4,
    DeviceIoControlSetOutputReport = 5,
}

public sealed record ArmouryTapPreFilterDiagnostics(
    string ProcessName,
    uint HidDSetFeatureCallCount,
    uint WriteFileCallCount,
    uint HidDSetOutputReportCallCount,
    uint DeviceIoControlSetFeatureCallCount,
    uint DeviceIoControlSetOutputReportCallCount,
    uint InvalidHandleCount,
    uint AttributeReadFailureCount,
    uint NonAsusDeviceCount,
    uint OtherAsusProductCount,
    uint UnderLengthCount,
    uint BoundedLengthCount,
    uint OverLengthCount,
    uint UnreadableBufferCount,
    uint ReportId5ACount,
    uint Prefix5AD1Count,
    uint RetainedRecordCount,
    uint NativeDroppedRecordCount,
    bool CounterSaturated);

public sealed record ArmouryTapRecord(
    string ProcessName,
    int Phase,
    int Ordinal,
    ArmouryTapApi Api,
    bool ApiResult,
    int LastError,
    byte[] Report);
