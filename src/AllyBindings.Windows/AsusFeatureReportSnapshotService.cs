using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AllyBindings.Core;

namespace AllyBindings.Windows;

/// <summary>
/// Unelevated, target-scoped report-0x5A snapshot evidence plane. It performs
/// read-only HID operations and has no reference to ETW, named pipes or writes.
/// </summary>
internal sealed class AsusFeatureReportSnapshotService
{
    private const int SnapshotSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<AsusFeatureReportSnapshotTarget> DiscoverTargetAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("ASUS feature-report snapshots are available only on Windows.");
        }

        await using var device = new AsusRearButtonHidDevice();
        var status = await device.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsSupportedModel) throw new InvalidOperationException(status.Message);
        if (!status.IsAvailable || status.DeviceIds.Count == 0)
        {
            throw new InvalidOperationException("No compatible ASUS feature-report 0x5A interface was found.");
        }
        return new(
            status.Model,
            status.DeviceIds.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            device.GetSnapshotInterfaceIdentityKeys().Order(StringComparer.Ordinal).ToArray());
    }

    public async Task<AsusFeatureReportSnapshotCapture> ReadStageAsync(
        AsusFeatureReportSnapshotTarget confirmedTarget,
        AsusFeatureReportSnapshotStage stage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedTarget);
        await using var device = new AsusRearButtonHidDevice();
        var status = await device.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var currentTarget = new AsusFeatureReportSnapshotTarget(
            status.Model,
            status.DeviceIds.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            device.GetSnapshotInterfaceIdentityKeys().Order(StringComparer.Ordinal).ToArray());
        if (!status.IsSupportedModel || !status.IsAvailable || !IsSameTarget(confirmedTarget, currentTarget))
        {
            throw new InvalidOperationException(
                "The confirmed ASUS HID identity changed before the read-only snapshot. No report was accepted.");
        }

        var result = await device.ReadFeatureReportAsync(cancellationToken).ConfigureAwait(false);
        return new(stage, result);
    }

    public async Task<AsusFeatureReportSnapshotResult> CompleteAsync(
        AsusFeatureReportSnapshotTarget confirmedTarget,
        IReadOnlyList<AsusFeatureReportSnapshotCapture> captures,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedTarget);
        ArgumentNullException.ThrowIfNull(captures);
        var currentTarget = await DiscoverTargetAsync(cancellationToken).ConfigureAwait(false);
        if (!IsSameTarget(confirmedTarget, currentTarget))
        {
            throw new InvalidOperationException(
                "The confirmed ASUS HID identity changed before snapshot completion. No bundle was accepted.");
        }

        var analysis = AsusFeatureReportSnapshotAnalyzer.Analyze(captures);
        var evidenceBytes = SerializeJson(new
        {
            snapshotSchemaVersion = SnapshotSchemaVersion,
            diagnosticOnly = true,
            hardwareUnlockEvidence = false,
            captures = captures.Select(capture => new
            {
                capture.Stage,
                capture.Result.Attempted,
                capture.Result.Succeeded,
                capture.Result.Message,
                reads = capture.Result.Reads.Select(read => new
                {
                    read.DeviceId,
                    reportLength = read.Report.Length,
                    reportHex = Convert.ToHexString(read.Report.AsSpan()),
                    read.Sha256,
                    read.Message,
                }),
            }),
            analysis,
        });
        var evidenceHash = Hash(evidenceBytes);
        var manifestBytes = SerializeJson(new
        {
            snapshotSchemaVersion = SnapshotSchemaVersion,
            capturedAtUtc = DateTimeOffset.UtcNow,
            applicationVersion = GetApplicationVersion(),
            source = "read-only HidSharp GetFeature(0x5A) snapshot",
            selectedAsusHid = confirmedTarget,
            evidence = new
            {
                file = "snapshot.json",
                sha256 = evidenceHash,
                bytes = evidenceBytes.Length,
                rawSystemTraceWritten = false,
                hardwareWriteAttempted = false,
                hardwareUnlockEvidence = false,
            },
            expectedProtocol = new
            {
                rearMappingPrefix = "5A D1 02 08 2C",
                minimumReportLength = AsusRearButtonProtocol.ReportLength,
                maximumReportLength = UsbEtwHidFeatureReportExtractor.MaximumWireReportLength,
                expectedReportId = "5A",
            },
            writeGates = new
            {
                customWritesApproved = ArmouryProtocolValidation.CustomWritesApproved,
                recoveryWritesApproved = ArmouryProtocolValidation.RecoveryWritesApproved,
            },
            review = new
            {
                required = true,
                minimumIndependentMatchingRuns = 2,
                userWritableBundleIsImmutableProvenance = false,
            },
        });
        var readmeBytes = Encoding.UTF8.GetBytes(BuildReadme(analysis));

        cancellationToken.ThrowIfCancellationRequested();
        var captureRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AllyBindings",
            "captures");
        Directory.CreateDirectory(captureRoot);
        var bundlePath = Path.Combine(
            captureRoot,
            $"ally-bindings-feature-snapshot-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");
        var bundleSha256 = CreateBundle(
            bundlePath,
            ("snapshot.json", evidenceBytes),
            ("manifest.json", manifestBytes),
            ("README.txt", readmeBytes));
        return new(
            bundlePath,
            Hash: bundleSha256,
            SuccessfulStageCount: captures.Count(capture => capture.Result.Succeeded),
            analysis);
    }

    private static bool IsSameTarget(
        AsusFeatureReportSnapshotTarget expected,
        AsusFeatureReportSnapshotTarget actual) =>
        string.Equals(expected.Model, actual.Model, StringComparison.OrdinalIgnoreCase) &&
        expected.DeviceIds.Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(actual.DeviceIds.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase) &&
        expected.InterfaceIdentityKeys.Order(StringComparer.Ordinal)
            .SequenceEqual(actual.InterfaceIdentityKeys.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static byte[] SerializeJson<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string GetApplicationVersion()
    {
        var assembly = typeof(AsusFeatureReportSnapshotService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static string BuildReadme(AsusFeatureReportSnapshotAnalysis analysis) =>
        $"Ally Bindings read-only ASUS report 0x5A snapshot{Environment.NewLine}" +
        $"REVIEW REQUIRED — NOT HARDWARE UNLOCK EVIDENCE{Environment.NewLine}{Environment.NewLine}" +
        $"Readable stages: {(analysis.AllStagesReadable ? "4/4" : "incomplete")}{Environment.NewLine}" +
        $"Clean-room candidate sequence matched: {analysis.CandidateSequenceMatched}{Environment.NewLine}" +
        $"Reset returned to baseline: {analysis.ResetReturnedToBaseline?.ToString() ?? "unknown"}{Environment.NewLine}{Environment.NewLine}" +
        string.Join(Environment.NewLine, analysis.Reasons.Select(reason => $"- {reason}")) + Environment.NewLine +
        "This instrument issued only bounded, target-scoped HID GET_FEATURE requests for report 0x5A. " +
        "It used no elevation, ETW, named pipe, driver, raw system trace, SET_FEATURE call or M1/M2 write. " +
        "The report bytes are private controller-configuration diagnostics. This user-writable ZIP and its hashes are integrity aids, not immutable provenance. " +
        "Require at least two independent matching physical runs and human review before any protocol discussion. Both hardware write gates remain source locked.";

    private static string CreateBundle(
        string bundlePath,
        params (string Name, byte[] Content)[] artifacts)
    {
        var temporaryPath = $"{bundlePath}.tmp-{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var artifact in artifacts)
                {
                    var entry = archive.CreateEntry(artifact.Name, CompressionLevel.Optimal);
                    using var destination = entry.Open();
                    destination.Write(artifact.Content);
                }
            }
            File.Move(temporaryPath, bundlePath);
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundlePath))).ToLowerInvariant();
        }
        catch
        {
            try { File.Delete(temporaryPath); }
            catch { }
            throw;
        }
    }
}

internal sealed record AsusFeatureReportSnapshotTarget(
    string Model,
    IReadOnlyList<string> DeviceIds,
    [property: JsonIgnore] IReadOnlyList<string> InterfaceIdentityKeys);

internal sealed record AsusFeatureReportSnapshotResult(
    string BundlePath,
    string Hash,
    int SuccessfulStageCount,
    AsusFeatureReportSnapshotAnalysis Analysis);
