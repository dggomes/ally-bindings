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
    public const ushort WireVersion = 1;
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

    public static bool IsSupportedDevice(ushort vendorId, ushort productId) =>
        vendorId == AsusVendorId && productId == AllyProductId;
}

public enum ArmouryTapApi : byte
{
    HidDSetFeature = 1,
    KernelBaseWriteFile = 2,
}

public sealed record ArmouryTapRecord(
    string ProcessName,
    int Phase,
    int Ordinal,
    ArmouryTapApi Api,
    bool ApiResult,
    int LastError,
    byte[] Report);
