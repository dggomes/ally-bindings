using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AllyBindings.Core;
using AllyBindings.Windows;

namespace AllyBindings.HardwareValidator;

internal static class Program
{
    private const int Success = 0;
    private const int UnsupportedHost = 2;
    private const int TargetRejected = 3;
    private const int ConfirmationRejected = 4;
    private const int WriteFailed = 5;
    private const int UsageError = 64;

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("Ally Bindings — private M1/M2 physical validator");
        Console.WriteLine("This is not the Ally Bindings application and does not unlock application writes.");
        Console.WriteLine();

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This validator runs only on Windows.");
            return UnsupportedHost;
        }

        if (args.Length != 1 || args[0] is "--help" or "-h" or "help")
        {
            PrintUsage();
            return args.Length == 1 ? Success : UsageError;
        }

        var command = args[0];
        if (command is not AsusRearButtonLabValidation.InspectCommand and
            not AsusRearButtonLabValidation.WriteCommand)
        {
            Console.Error.WriteLine($"Unknown command '{command}'.");
            PrintUsage();
            return UsageError;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        await using var device = new AsusRearButtonHidDevice();
        var status = await device.InitializeAsync(cancellation.Token).ConfigureAwait(false);
        var interfaceCount = device.GetSnapshotInterfaceIdentityKeys().Count;
        PrintTarget(status, interfaceCount);

        if (!status.IsSupportedModel || !status.IsAvailable)
        {
            Console.Error.WriteLine("Target validation failed. No hardware write was attempted.");
            return TargetRejected;
        }

        if (command == AsusRearButtonLabValidation.InspectCommand)
        {
            Console.WriteLine("INSPECT PASSED: no controller setting was read or changed.");
            return Success;
        }

        var report = AsusRearButtonLabValidation.BuildOneShotReport();
        var packetHash = Convert.ToHexString(SHA256.HashData(report)).ToLowerInvariant();
        var audit = LabAudit.Create(status.Model, interfaceCount, packetHash, Convert.ToHexString(report));
        string auditPath;
        try
        {
            auditPath = await LabAuditStore.WriteAsync(audit, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not create the pre-write audit record ({ex.GetType().Name}). No hardware write was attempted.");
            return TargetRejected;
        }

        Console.WriteLine();
        Console.WriteLine("DANGER — THIS COMMAND CHANGES THE CONTROLLER'S M1/M2 CONFIGURATION.");
        Console.WriteLine("Before continuing:");
        Console.WriteLine("  1. Close Armoury Crate, games, launchers, and anti-cheat software.");
        Console.WriteLine("  2. Photograph or export the current Armoury M1/M2 assignments.");
        Console.WriteLine("  3. Keep Armoury Crate available to restore the assignments after testing.");
        Console.WriteLine("The validator has no reset command and will not guess your previous configuration.");
        Console.WriteLine();
        Console.WriteLine("Fixed operation: M1=A, M2=B");
        Console.WriteLine($"Packet SHA-256: {packetHash}");
        Console.WriteLine($"Exact 50-byte packet: {Convert.ToHexString(report)}");
        Console.WriteLine($"Pre-write audit: {auditPath}");
        Console.WriteLine();
        Console.WriteLine($"Type exactly: {AsusRearButtonLabValidation.ConfirmationPhrase}");
        Console.Write("> ");
        var confirmation = Console.ReadLine();

        var authorization = AsusRearButtonLabValidation.Authorize(
            command,
            confirmation,
            Console.IsInputRedirected,
            interfaceCount);
        if (!authorization.Approved)
        {
            audit = audit with { Outcome = "confirmation-rejected", Detail = authorization.Message };
            await LabAuditStore.WriteAsync(audit, CancellationToken.None).ConfigureAwait(false);
            Console.Error.WriteLine(authorization.Message);
            return ConfirmationRejected;
        }

        try
        {
            await LabAuditStore.ClaimOneShotAsync(audit, cancellation.Token).ConfigureAwait(false);
            audit = audit with
            {
                Outcome = "authorized-write-pending",
                Detail = "The durable one-shot claim was created; the HID outcome is pending.",
                RecoveryRequired = true,
            };
            await LabAuditStore.WriteAsync(audit, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            audit = audit with
            {
                Outcome = "one-shot-claim-rejected",
                Detail = $"The durable one-shot claim could not be created ({ex.GetType().Name}); no HID write was attempted.",
            };
            await LabAuditStore.WriteAsync(audit, CancellationToken.None).ConfigureAwait(false);
            Console.Error.WriteLine(audit.Detail);
            return ConfirmationRejected;
        }

        AsusRearButtonWriteResult write;
        try
        {
            write = await device.WriteFeatureReportAsync(report, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            audit = audit with
            {
                Outcome = "cancelled-outcome-unknown",
                Detail = "Cancellation occurred around the HID operation; recovery through Armoury remains required.",
                RecoveryRequired = true,
            };
            await LabAuditStore.WriteAsync(audit, CancellationToken.None).ConfigureAwait(false);
            Console.Error.WriteLine(audit.Detail);
            return WriteFailed;
        }
        catch (Exception ex)
        {
            audit = audit with
            {
                Outcome = "write-error",
                Detail = $"{ex.GetType().Name}: {ex.Message}",
                RecoveryRequired = true,
            };
            await LabAuditStore.WriteAsync(audit, CancellationToken.None).ConfigureAwait(false);
            Console.Error.WriteLine("The HID operation failed. Its hardware outcome may be unknown; restore through Armoury.");
            return WriteFailed;
        }

        audit = audit with
        {
            Outcome = write.Succeeded == 1
                ? "hid-api-accepted"
                : write.Message.Contains("outcome is unknown", StringComparison.OrdinalIgnoreCase)
                    ? "hid-outcome-unknown"
                    : "hid-api-rejected",
            Detail = write.Message,
            AttemptedInterfaces = write.Attempted,
            SuccessfulInterfaces = write.Succeeded,
            RecoveryRequired = write.Attempted > 0,
        };
        await LabAuditStore.WriteAsync(audit, CancellationToken.None).ConfigureAwait(false);

        if (write.Succeeded != 1)
        {
            Console.Error.WriteLine($"WRITE NOT CONFIRMED: {write.Message}");
            Console.Error.WriteLine("If any attempt occurred, restore through Armoury before further testing.");
            return WriteFailed;
        }

        Console.WriteLine();
        Console.WriteLine("HID API ACCEPTED THE ONE-SHOT PACKET. This is not yet physical proof.");
        Console.WriteLine("Next:");
        Console.WriteLine("  1. Open joy.cpl or another safe local controller tester.");
        Console.WriteLine("  2. Verify M1 registers only A and M2 registers only B.");
        Console.WriteLine("  3. Restore your original/default assignments in Armoury Crate.");
        Console.WriteLine("  4. Verify both paddles again after Armoury restoration.");
        Console.WriteLine($"Audit requiring recovery confirmation: {auditPath}");
        return Success;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  AllyBindings.HardwareValidator.exe inspect");
        Console.WriteLine("  AllyBindings.HardwareValidator.exe write-m1-a-m2-b");
        Console.WriteLine();
        Console.WriteLine("Run inspect first. The write command is fixed, interactive, one-shot, and has no reset option.");
    }

    private static void PrintTarget(AsusRearButtonDeviceStatus status, int interfaceCount)
    {
        Console.WriteLine($"Model: {status.Model}");
        Console.WriteLine($"Supported ROG Ally identity: {status.IsSupportedModel}");
        Console.WriteLine($"Compatible openable report-0x5A interfaces: {interfaceCount}");
        Console.WriteLine(status.Message);
    }
}

internal sealed record LabAudit(
    int SchemaVersion,
    string ValidatorVersion,
    string SessionId,
    DateTimeOffset CreatedAtUtc,
    string Model,
    int CompatibleInterfaceCount,
    string FixedMapping,
    string PacketSha256,
    string PacketHex,
    string Outcome,
    string Detail,
    int AttemptedInterfaces,
    int SuccessfulInterfaces,
    bool RecoveryRequired,
    bool ArmouryRecoveryConfirmed)
{
    public static LabAudit Create(string model, int interfaceCount, string packetHash, string packetHex) =>
        new(
            SchemaVersion: 1,
            ValidatorVersion: Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            SessionId: Guid.NewGuid().ToString("N"),
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Model: model,
            CompatibleInterfaceCount: interfaceCount,
            FixedMapping: "M1=A;M2=B",
            PacketSha256: packetHash,
            PacketHex: packetHex,
            Outcome: "not-attempted",
            Detail: "Pre-write audit created; no HID write has occurred yet.",
            AttemptedInterfaces: 0,
            SuccessfulInterfaces: 0,
            RecoveryRequired: false,
            ArmouryRecoveryConfirmed: false);
}

internal static class LabAuditStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task ClaimOneShotAsync(LabAudit audit, CancellationToken cancellationToken)
    {
        var root = GetRoot();
        Directory.CreateDirectory(root);
        var claimPath = Path.Combine(root, "one-shot-claimed.json");
        var claim = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            audit.SessionId,
            claimedAtUtc = DateTimeOffset.UtcNow,
            audit.PacketSha256,
            purpose = "fixed M1=A;M2=B physical validation",
        }, JsonOptions);
        await using var stream = new FileStream(
            claimPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
        await stream.WriteAsync(claim, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> WriteAsync(LabAudit audit, CancellationToken cancellationToken)
    {
        var root = GetRoot();
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"{audit.SessionId}.json");
        var temporaryPath = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(audit, JsonOptions);
        await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, path, overwrite: true);
        return path;
    }

    private static string GetRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AllyBindings",
        "hardware-validation");
}
