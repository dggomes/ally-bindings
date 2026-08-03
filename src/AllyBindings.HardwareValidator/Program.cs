using System.Reflection;
using System.Text.Json;

namespace AllyBindings.HardwareValidator;

internal static class Program
{
    private const int Success = 0;
    private const int TargetRejected = 3;
    private const int ConfirmationRejected = 4;
    private const int WriteFailed = 5;
    private const int AuditPersistenceFailed = 6;
    private const int UsageError = 64;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 ||
            (args[0] != HardwareLabPolicy.InspectCommand && args[0] != HardwareLabPolicy.WriteCommand))
        {
            PrintUsage();
            return UsageError;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        LabTargetSnapshot target;
        try
        {
            target = await ExactRc73xaLabWriter.InspectAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Inspection cancelled; no HID write was attempted.");
            return TargetRejected;
        }

        PrintTarget(target);
        if (!target.Approved)
        {
            Console.Error.WriteLine("Exact target validation failed. No hardware write was attempted.");
            return TargetRejected;
        }

        var approvedOperation = HardwareLabPolicy.CreateApprovedOperation(target);
        Console.WriteLine($"Logical fixed command ({HardwareLabPolicy.LogicalReportLength} bytes): {Convert.ToHexString(HardwareLabPolicy.GetLogicalPacket())}");
        Console.WriteLine($"Logical SHA-256: {HardwareLabPolicy.LogicalPacketSha256}");
        Console.WriteLine($"Exact wire packet ({approvedOperation.WireLength} bytes): {approvedOperation.WireHex}");
        Console.WriteLine($"Exact wire SHA-256: {approvedOperation.WireSha256}");

        if (args[0] == HardwareLabPolicy.InspectCommand)
        {
            Console.WriteLine("Inspection only. No HID feature report was read or written.");
            Console.WriteLine("Close Armoury Crate and anti-cheat software before the write command.");
            return Success;
        }

        var audit = new LabAudit(
            SchemaVersion: 3,
            ValidatorVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            SessionId: Guid.NewGuid().ToString("N"),
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Model: target.Model,
            VendorId: HardwareLabPolicy.TargetVendorId,
            ProductId: HardwareLabPolicy.TargetProductId,
            CompatibleInterfaceCount: 1,
            FeatureReportLength: target.FeatureReportLength,
            FixedMapping: "M1=A; M2=B",
            LogicalPacketSha256: HardwareLabPolicy.LogicalPacketSha256,
            WirePacketHex: approvedOperation.WireHex,
            WirePacketSha256: approvedOperation.WireSha256,
            Outcome: "not-attempted",
            Detail: "Pre-write audit created before authorization.",
            AttemptedInterfaces: 0,
            SuccessfulInterfaces: 0,
            RecoveryRequired: false,
            ArmouryRecoveryConfirmed: false);

        string preWriteAuditPath;
        try
        {
            preWriteAuditPath = await LabAuditStore.WriteAsync(audit, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Pre-write audit failed ({ex.GetType().Name}); no HID write was attempted.");
            return AuditPersistenceFailed;
        }

        Console.WriteLine();
        Console.WriteLine("DANGER: this performs one experimental hardware write. It does not read current mappings.");
        Console.WriteLine("Before continuing: save/screenshot Armoury settings, then fully close Armoury Crate and anti-cheat software.");
        Console.WriteLine("Recovery is ONLY through Armoury Crate; this validator has no reset command.");
        Console.WriteLine($"Type exactly: {HardwareLabPolicy.ConfirmationPhrase}");
        Console.Write("> ");
        var confirmation = Console.ReadLine();

        var authorization = HardwareLabPolicy.Authorize(
            args[0], confirmation, Console.IsInputRedirected, compatibleInterfaceCount: 1);
        if (!authorization.Approved)
        {
            audit = audit with { Outcome = "confirmation-rejected", Detail = authorization.Message };
            _ = await PersistAuditBestEffortAsync(audit).ConfigureAwait(false);
            Console.Error.WriteLine(authorization.Message);
            return ConfirmationRejected;
        }

        try
        {
            await LabAuditStore.ClaimOneShotAsync(audit, cancellation.Token).ConfigureAwait(false);
            audit = audit with
            {
                Outcome = "authorized-write-pending",
                Detail = "Durable one-shot claim created; hardware outcome pending.",
                RecoveryRequired = true,
            };
            await LabAuditStore.WriteAsync(audit, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Durable claim/pending audit failed ({ex.GetType().Name}); no HID write was attempted.");
            return ConfirmationRejected;
        }

        LabWriteResult write;
        var operationEntered = false;
        try
        {
            operationEntered = true;
            write = await ExactRc73xaLabWriter.WriteAsync(
                approvedOperation,
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            audit = audit with
            {
                Outcome = "cancelled-outcome-unknown",
                Detail = "Cancellation occurred around SET_FEATURE; hardware outcome unknown.",
                RecoveryRequired = true,
            };
            _ = await PersistAuditBestEffortAsync(audit).ConfigureAwait(false);
            Console.Error.WriteLine(audit.Detail);
            return WriteFailed;
        }
        catch (Exception ex)
        {
            audit = audit with
            {
                Outcome = "write-error-outcome-unknown",
                Detail = $"{ex.GetType().Name}: {ex.Message}",
                RecoveryRequired = true,
            };
            _ = await PersistAuditBestEffortAsync(audit).ConfigureAwait(false);
            Console.Error.WriteLine("SET_FEATURE failed or its outcome is unknown.");
            return WriteFailed;
        }
        finally
        {
            if (operationEntered)
            {
                Console.Error.WriteLine("RECOVERY REQUIRED — restore the original mappings through Armoury Crate before any further test.");
            }
        }

        audit = audit with
        {
            Outcome = write.Succeeded == 1
                ? "hid-api-accepted"
                : write.Attempted > 0 ? "hid-outcome-rejected-or-unknown" : "hid-not-attempted-after-revalidation",
            Detail = write.Message,
            AttemptedInterfaces = write.Attempted,
            SuccessfulInterfaces = write.Succeeded,
            RecoveryRequired = true,
        };
        var outcomeAuditPath = await PersistAuditBestEffortAsync(audit).ConfigureAwait(false);

        if (write.Succeeded != 1)
        {
            Console.Error.WriteLine($"WRITE NOT CONFIRMED: {write.Message}");
            if (outcomeAuditPath is null)
                Console.Error.WriteLine("Outcome audit also failed; the recovery warning remains authoritative.");
            return WriteFailed;
        }

        Console.WriteLine("HID API ACCEPTED THE SOLE FIXED PACKET. This is not physical proof.");
        Console.WriteLine("1. Verify in joy.cpl: M1 registers only A; M2 registers only B.");
        Console.WriteLine("2. Check for duplicate or stuck inputs.");
        Console.WriteLine("3. Restore the original assignments in Armoury Crate.");
        Console.WriteLine("4. Verify both paddles again after restoration.");
        Console.WriteLine($"Pre-write audit: {preWriteAuditPath}");
        if (outcomeAuditPath is null)
        {
            Console.Error.WriteLine("WRITE SUCCEEDED, BUT OUTCOME AUDIT PERSISTENCE FAILED.");
            return AuditPersistenceFailed;
        }
        Console.WriteLine($"Outcome audit: {outcomeAuditPath}");
        return Success;
    }

    private static async Task<string?> PersistAuditBestEffortAsync(LabAudit audit)
    {
        try
        {
            return await LabAuditStore.WriteAsync(audit, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Audit persistence failed ({ex.GetType().Name}); this does not change the hardware/recovery outcome.");
            return null;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  AllyBindings.HardwareValidator.exe inspect");
        Console.WriteLine("  AllyBindings.HardwareValidator.exe write-m1-a-m2-b");
        Console.WriteLine("The write command is fixed, interactive, one-shot, and has no reset option.");
    }

    private static void PrintTarget(LabTargetSnapshot target)
    {
        Console.WriteLine($"Model: {target.Model}");
        Console.WriteLine($"Exact RC73XA target approved: {target.Approved}");
        Console.WriteLine($"Compatible VID_0B05/PID_1B4C interfaces: {(target.Approved ? 1 : 0)}");
        Console.WriteLine($"Feature report length: {target.FeatureReportLength}");
        Console.WriteLine(target.Message);
    }
}

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
    {
        var root = GetRoot();
        Directory.CreateDirectory(root);
        var safeOutcome = string.Concat(audit.Outcome.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character == '-' ? character : '-'));
        var path = Path.Combine(root, $"{audit.SessionId}-{safeOutcome}.json");
        await WriteCreateNewAsync(path, JsonSerializer.SerializeToUtf8Bytes(audit, JsonOptions), cancellationToken).ConfigureAwait(false);
        return path;
    }

    internal static Task ClaimOneShotAsync(LabAudit audit, CancellationToken cancellationToken)
    {
        var root = GetRoot();
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
