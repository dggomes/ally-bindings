using System.Text.Json;

namespace AllyBindings.HardwareValidator;

internal sealed record LabAudit(
    int SchemaVersion,
    string ValidatorVersion,
    string SessionId,
    DateTimeOffset CreatedAtUtc,
    string Model,
    int VendorId,
    int ProductId,
    int CompatibleInterfaceCount,
    int FeatureReportLength,
    string FixedMapping,
    string LogicalPacketSha256,
    string WirePacketHex,
    string WirePacketSha256,
    string Outcome,
    string Detail,
    int AttemptedInterfaces,
    int SuccessfulInterfaces,
    bool RecoveryRequired,
    bool ArmouryRecoveryConfirmed);

internal static class LabAuditStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task<string> WriteAsync(LabAudit audit, CancellationToken cancellationToken)
        => await WriteAtRootAsync(GetRoot(), audit, cancellationToken).ConfigureAwait(false);

    private static async Task<string> WriteAtRootAsync(
        string root,
        LabAudit audit,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var safeOutcome = string.Concat(audit.Outcome.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character == '-' ? character : '-'));
        var path = Path.Combine(root, $"{audit.SessionId}-{safeOutcome}.json");
        await WriteCreateNewAsync(path, JsonSerializer.SerializeToUtf8Bytes(audit, JsonOptions), cancellationToken).ConfigureAwait(false);
        return path;
    }

    internal static Task ClaimOneShotAsync(LabAudit audit, CancellationToken cancellationToken)
        => ClaimOneShotAtRootAsync(GetRoot(), audit, cancellationToken);

    private static Task ClaimOneShotAtRootAsync(
        string root,
        LabAudit audit,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var claim = new
        {
            schemaVersion = 3,
            claimedAtUtc = DateTimeOffset.UtcNow,
            audit.SessionId,
            audit.ValidatorVersion,
            audit.Model,
            audit.ProductId,
            audit.WirePacketSha256,
            recoveryRequired = true,
            armouryRecoveryConfirmed = false,
        };
        return WriteCreateNewAsync(
            Path.Combine(root, "one-shot-claimed.json"),
            JsonSerializer.SerializeToUtf8Bytes(claim, JsonOptions),
            cancellationToken);
    }

#if HARDWARE_VALIDATOR_TESTS
    internal static Task<string> WriteForTestAsync(string root, LabAudit audit, CancellationToken cancellationToken) =>
        WriteAtRootAsync(root, audit, cancellationToken);

    internal static Task ClaimOneShotForTestAsync(string root, LabAudit audit, CancellationToken cancellationToken) =>
        ClaimOneShotAtRootAsync(root, audit, cancellationToken);
#endif

    private static async Task WriteCreateNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
        });
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static string GetRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "AllyBindings",
        "HardwareValidator");
}
