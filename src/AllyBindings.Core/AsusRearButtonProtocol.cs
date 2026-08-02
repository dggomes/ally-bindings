using System.Collections.Immutable;

namespace AllyBindings.Core;

public static class ArmouryProtocolValidation
{
    // Deliberately locked until a physical Ally capture proves that Armoury
    // Crate emits the same report shape and native-reset bytes we build.
    public static bool CustomWritesApproved => false;
    public static bool RecoveryWritesApproved => false;

    internal static bool IsOperationApproved(
        bool isRecoveryReset,
        bool customWritesApproved,
        bool recoveryWritesApproved) =>
        isRecoveryReset ? recoveryWritesApproved : customWritesApproved && recoveryWritesApproved;

    internal static bool IsOperationApproved(bool isRecoveryReset) =>
        IsOperationApproved(isRecoveryReset, CustomWritesApproved, RecoveryWritesApproved);
    public const string GateMessage =
        "ASUS M1/M2 writes are locked pending passive Armoury Crate protocol validation.";
}

/// <summary>
/// Clean-room packet builder for the ASUS embedded-controller M1/M2 mapping zone.
///
/// The wire format is independently corroborated by G-Helper and Handheld
/// Companion: feature report 0x5A, command 0xD1, mapping zone 0x08. This class
/// contains protocol facts only; it does not include code from either project.
/// </summary>
public static class AsusRearButtonProtocol
{
    public const byte FeatureReportId = 0x5A;
    public const int ReportLength = 50;

    private const int M2PrimaryOffset = 5;
    private const int M2SecondaryOffset = 16;
    private const int M1PrimaryOffset = 27;
    private const int M1SecondaryOffset = 38;

    private static readonly IReadOnlyDictionary<ControllerButton, byte> ControllerCodes =
        new Dictionary<ControllerButton, byte>
        {
            [ControllerButton.A] = 0x01,
            [ControllerButton.B] = 0x02,
            [ControllerButton.X] = 0x03,
            [ControllerButton.Y] = 0x04,
            [ControllerButton.LeftBumper] = 0x05,
            [ControllerButton.RightBumper] = 0x06,
            [ControllerButton.LeftStick] = 0x07,
            [ControllerButton.RightStick] = 0x08,
            [ControllerButton.DPadUp] = 0x09,
            [ControllerButton.DPadDown] = 0x0A,
            [ControllerButton.DPadLeft] = 0x0B,
            [ControllerButton.DPadRight] = 0x0C,
            [ControllerButton.LeftTrigger] = 0x0D,
            [ControllerButton.RightTrigger] = 0x0E,
            [ControllerButton.View] = 0x11,
            [ControllerButton.Menu] = 0x12,
        };

    public static byte[] BuildMappingReport(ControllerButton m1Target, ControllerButton m2Target)
    {
        ValidateRearTarget(ControllerButton.M1, m1Target);
        ValidateRearTarget(ControllerButton.M2, m2Target);

        var report = new byte[ReportLength];
        report[0] = FeatureReportId;
        report[1] = 0xD1;
        report[2] = 0x02;
        report[3] = 0x08;
        report[4] = 0x2C;

        // ASUS orders the physical paddles as M2 then M1. Applying the same
        // action in both slots makes each paddle an independent action rather
        // than leaving a stale Armoury secondary-function assignment behind.
        WriteAction(report, M2PrimaryOffset, ControllerButton.M2, m2Target);
        WriteAction(report, M2SecondaryOffset, ControllerButton.M2, m2Target);
        WriteAction(report, M1PrimaryOffset, ControllerButton.M1, m1Target);
        WriteAction(report, M1SecondaryOffset, ControllerButton.M1, m1Target);
        return report;
    }

    /// <summary>
    /// Builds the best-known native M1/M2 modifier mapping corroborated by
    /// independent implementations. This is not a read-back of the user's
    /// Armoury configuration and must be physically validated per firmware.
    /// </summary>
    public static byte[] BuildNativeResetReport() =>
        BuildMappingReport(ControllerButton.M1, ControllerButton.M2);

    public static bool MatchesWireReport(ReadOnlySpan<byte> captured, ReadOnlySpan<byte> expected)
    {
        if (captured.Length < expected.Length || !captured[..expected.Length].SequenceEqual(expected))
        {
            return false;
        }
        return captured[expected.Length..].IndexOfAnyExcept((byte)0) < 0;
    }

    private static void ValidateRearTarget(ControllerButton source, ControllerButton target)
    {
        if (!ControllerButtons.IsValidBinding(source, target))
        {
            throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                $"{source} cannot be mapped to {target} by the ASUS rear-button protocol.");
        }
    }

    private static void WriteAction(
        Span<byte> report,
        int offset,
        ControllerButton source,
        ControllerButton target)
    {
        if (target == source)
        {
            // ASUS's own M2/M1 modifier actions use keyboard-like action type
            // 0x02 and codes 0x8E/0x8F respectively.
            report[offset] = 0x02;
            report[offset + 2] = source == ControllerButton.M1 ? (byte)0x8F : (byte)0x8E;
            return;
        }

        if (!ControllerCodes.TryGetValue(target, out var code))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported ASUS controller action.");
        }

        report[offset] = 0x01;
        report[offset + 1] = code;
    }
}

public sealed record AsusRearButtonDeviceStatus(
    bool IsSupportedModel,
    bool IsAvailable,
    string Model,
    IReadOnlyList<string> DeviceIds,
    string Message);

public sealed record AsusRearButtonWriteResult(
    int Attempted,
    int Succeeded,
    string Message);

public sealed record AsusRearButtonReadResult(
    bool Attempted,
    bool Succeeded,
    IReadOnlyList<AsusFeatureReportRead> Reads,
    string Message);

public sealed record AsusFeatureReportRead(
    string DeviceId,
    ImmutableArray<byte> Report,
    string Sha256,
    string Message);

public interface IAsusRearButtonDevice : IAsyncDisposable
{
    Task<AsusRearButtonDeviceStatus> InitializeAsync(CancellationToken cancellationToken = default);
    AsusRearButtonDeviceStatus GetStatus();
    Task<AsusRearButtonReadResult> ReadFeatureReportAsync(
        CancellationToken cancellationToken = default);
    Task<AsusRearButtonWriteResult> WriteFeatureReportAsync(
        byte[] report,
        CancellationToken cancellationToken = default);
}
