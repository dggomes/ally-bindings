using System.Security.Cryptography;

namespace AllyBindings.HardwareValidator;

internal static class HardwareLabPolicy
{
    internal const int TargetVendorId = 0x0B05;
    internal const int TargetProductId = 0x1B4C;
    internal const byte FeatureReportId = 0x5A;
    internal const int LogicalReportLength = 50;
    internal const int MaximumWireReportLength = 64;
    internal const string InspectCommand = "inspect";
    internal const string WriteCommand = "write-m1-a-m2-b";
    internal const string ConfirmationPhrase = "I SAVED SETTINGS; WRITE M1=A M2=B";
    internal const string LogicalPacketSha256 = "fb0f2ac8167350edf147fb839be2306ccb15494c824a44badeff7aad083cf38b";

    private static readonly byte[] LogicalPacket =
    [
        0x5A, 0xD1, 0x02, 0x08, 0x2C, 0x01, 0x02, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    internal static byte[] BuildWirePacket(int featureReportLength)
    {
        if (featureReportLength is < LogicalReportLength or > MaximumWireReportLength)
        {
            throw new ArgumentOutOfRangeException(nameof(featureReportLength));
        }

        var wirePacket = new byte[featureReportLength];
        LogicalPacket.CopyTo(wirePacket, 0);
        return wirePacket;
    }

    internal static byte[] GetLogicalPacket() => LogicalPacket.ToArray();

    internal static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    internal static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static ApprovedOperation CreateApprovedOperation(LabTargetSnapshot target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.Approved || string.IsNullOrWhiteSpace(target.InterfaceIdentityKey))
            throw new ArgumentException("An approved exact-target snapshot is required.", nameof(target));

        return new ApprovedOperation(target, BuildWirePacket(target.FeatureReportLength));
    }

    internal sealed class ApprovedOperation
    {
        private readonly byte[] wirePacket;

        internal ApprovedOperation(LabTargetSnapshot target, byte[] wirePacket)
        {
            Target = target;
            this.wirePacket = wirePacket.ToArray();
            WireHex = ToHex(this.wirePacket);
            WireSha256 = Sha256Hex(this.wirePacket);
        }

        internal LabTargetSnapshot Target { get; }

        internal int WireLength => wirePacket.Length;

        internal string WireHex { get; }

        internal string WireSha256 { get; }

        internal byte[] CopyWirePacket()
        {
            var expected = BuildWirePacket(Target.FeatureReportLength);
            if (!wirePacket.AsSpan().SequenceEqual(expected) ||
                !string.Equals(WireHex, ToHex(wirePacket), StringComparison.Ordinal) ||
                !string.Equals(WireSha256, Sha256Hex(wirePacket), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Approved wire evidence no longer matches the fixed packet.");
            }

            return wirePacket.ToArray();
        }
    }

    internal static bool IsApprovedProductName(string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName)) return false;
        var normalized = productName.Trim();
        return normalized.Equals("RC73XA", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("RC73XA_RC73XA", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("ROG Xbox Ally X RC73XA_RC73XA", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsApprovedInterface(
        int vendorId,
        int productId,
        int maxFeatureReportLength,
        int descriptorFeatureReportLength) =>
        vendorId == TargetVendorId &&
        productId == TargetProductId &&
        maxFeatureReportLength is >= LogicalReportLength and <= MaximumWireReportLength &&
        descriptorFeatureReportLength is >= LogicalReportLength and <= MaximumWireReportLength;

    internal static LabAuthorization Authorize(
        string? command,
        string? confirmation,
        bool inputRedirected,
        int compatibleInterfaceCount)
    {
        if (!string.Equals(command, WriteCommand, StringComparison.Ordinal))
            return new(false, "The requested command is not the sole approved write command.");
        if (inputRedirected)
            return new(false, "Redirected input is forbidden; confirmation must be typed interactively.");
        if (compatibleInterfaceCount != 1)
            return new(false, "Exactly one positively identified RC73XA/PID_1B4C interface is required.");
        if (!string.Equals(confirmation, ConfirmationPhrase, StringComparison.Ordinal))
            return new(false, "The exact confirmation phrase was not supplied.");
        return new(true, "Exact one-shot authorization accepted.");
    }
}

internal sealed record LabAuthorization(bool Approved, string Message);

internal sealed record LabTargetSnapshot(
    bool Approved,
    string Model,
    string InterfaceIdentityKey,
    int FeatureReportLength,
    string Message);

internal sealed record LabWriteResult(int Attempted, int Succeeded, string Message);
