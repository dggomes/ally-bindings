using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AllyBindings.Core;

namespace AllyBindings.Windows;

internal sealed class ArmouryCaptureService
{
    private static readonly TimeSpan CaptureStartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CaptureStopTimeout = TimeSpan.FromSeconds(30);
    private const int MaximumPipeEnvelopeCharacters = 512 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<ArmouryCaptureTarget> DiscoverTargetAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The integrated USB ETW logger is available only on Windows.");
        }

        var device = new AsusRearButtonHidDevice();
        var status = await device.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsSupportedModel)
        {
            throw new InvalidOperationException(status.Message);
        }
        if (!status.IsAvailable || status.DeviceIds.Count == 0)
        {
            throw new InvalidOperationException(
                "No compatible ASUS feature-report 0x5A interface was found. No ETW session was started.");
        }
        return new(status.Model, status.DeviceIds.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<ArmouryCaptureSession> StartAsync(
        ArmouryCaptureTarget confirmedTarget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedTarget);
        var sessionId = Guid.NewGuid();
        ArmouryCaptureDiagnostics.Record(sessionId, "parent-capture-starting");
        NamedPipeServerStream? pipe = null;
        Process? helper = null;
        try
        {
            var currentTarget = await DiscoverTargetAsync(cancellationToken).ConfigureAwait(false);
            if (!IsSameTarget(confirmedTarget, currentTarget))
            {
                throw new InvalidOperationException(
                    "The confirmed ASUS HID identity changed before ETW capture began. No capture was started.");
            }

            pipe = ArmouryEtwCapturePipe.CreateServer(sessionId);
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Windows did not expose the current Ally Bindings executable path.");
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add(ArmouryEtwCaptureHelper.HelperArgument);
            startInfo.ArgumentList.Add(sessionId.ToString("D"));
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            helper = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the elevated in-app ETW capture helper.");
            ArmouryCaptureDiagnostics.Record(sessionId, "parent-helper-launched");

            var connection = await WaitForReadyAsync(sessionId, helper, pipe, cancellationToken).ConfigureAwait(false);
            ArmouryCaptureDiagnostics.Record(sessionId, "parent-ready-received");
            var directory = Path.Combine(
                ArmouryEtwCapturePipe.GetCaptureRoot(),
                $"armoury-etw-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{sessionId:D}");
            Directory.CreateDirectory(directory);
            var session = new ArmouryCaptureSession(
                sessionId,
                helper,
                pipe,
                connection.Reader,
                connection.Writer,
                directory,
                confirmedTarget,
                connection.Ready.EnabledProviders.Select(provider => provider.Name).ToArray());
            session.RecordAction("capture-started");
            return session;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ArmouryCaptureDiagnostics.Delete(sessionId);
            helper?.Dispose();
            pipe?.Dispose();
            throw new OperationCanceledException(
                "Windows elevation was cancelled. The temporary ETW logger was not started and no USB data was retained.",
                ex,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ArmouryCaptureDiagnostics.Delete(sessionId);
            if (helper is not null)
            {
                StopHelper(helper);
                helper.Dispose();
            }
            pipe?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            var helperExitCode = TryGetExitCode(helper);
            ArmouryCaptureDiagnostics.Record(sessionId, "parent-start-failed", ex, helperExitCode);
            if (helper is not null)
            {
                StopHelper(helper);
                helper.Dispose();
            }
            pipe?.Dispose();
            if (ex is ArmouryCaptureException) throw;
            throw new ArmouryCaptureException(
                sessionId,
                $"The elevated ETW helper failed before capture became ready{FormatExitCode(helperExitCode)}.",
                ex);
        }
    }

    public async Task MarkActionAsync(
        ArmouryCaptureSession session,
        string action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var transition = action switch
        {
            "step-started-m1-a-m2-b" => (Phase: 1, Kind: UsbEtwCapturePhaseTransition.Start),
            "armoury-applied-m1-a-m2-b" => (Phase: 1, Kind: UsbEtwCapturePhaseTransition.End),
            "step-started-m1-x-m2-y" => (Phase: 2, Kind: UsbEtwCapturePhaseTransition.Start),
            "armoury-applied-m1-x-m2-y" => (Phase: 2, Kind: UsbEtwCapturePhaseTransition.End),
            "step-started-reset-to-default" => (Phase: 3, Kind: UsbEtwCapturePhaseTransition.Start),
            "armoury-reset-m1-m2-to-default" => (Phase: 3, Kind: UsbEtwCapturePhaseTransition.End),
            _ => (Phase: 0, Kind: UsbEtwCapturePhaseTransition.Start),
        };
        if (transition.Phase == 0)
        {
            session.RecordAction(action);
            return;
        }

        await session.PipeWriter.WriteLineAsync(
            UsbEtwCapturePhaseCommand.Format(transition.Phase, transition.Kind)).ConfigureAwait(false);
        var acknowledgement = await ReadEnvelopeAsync(
            session.SessionId,
            session.HelperProcess,
            session.PipeReader,
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        var expectedStarted = transition.Kind == UsbEtwCapturePhaseTransition.Start;
        if (!acknowledgement.Type.Equals("phase-ack", StringComparison.Ordinal) ||
            acknowledgement.Phase != transition.Phase ||
            acknowledgement.PhaseStarted != expectedStarted ||
            acknowledgement.BoundaryQpc is not > 0)
        {
            throw new InvalidDataException("The ETW helper did not acknowledge the requested capture phase boundary.");
        }
        session.RecordAction(action, acknowledgement.BoundaryQpc.Value);
        ArmouryCaptureDiagnostics.Record(
            session.SessionId,
            $"parent-phase-{transition.Kind.ToString().ToLowerInvariant()}-{transition.Phase}-acknowledged");
    }

    public async Task<ArmouryCaptureResult> CompleteAsync(
        ArmouryCaptureSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            return await CompleteCoreAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ArmouryCaptureException)
        {
            var helperExitCode = TryGetExitCode(session.HelperProcess);
            ArmouryCaptureDiagnostics.Record(session.SessionId, "parent-completion-failed", ex, helperExitCode);
            throw new ArmouryCaptureException(
                session.SessionId,
                $"The ETW capture could not be completed safely{FormatExitCode(helperExitCode)}.",
                ex);
        }
    }

    private async Task<ArmouryCaptureResult> CompleteCoreAsync(
        ArmouryCaptureSession session,
        CancellationToken cancellationToken)
    {
        ArmouryCaptureDiagnostics.Record(session.SessionId, "parent-stop-requested");
        await session.PipeWriter.WriteLineAsync("stop").ConfigureAwait(false);
        var envelope = await ReadEnvelopeAsync(
            session.SessionId,
            session.HelperProcess,
            session.PipeReader,
            CaptureStopTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!envelope.Type.Equals("result", StringComparison.Ordinal) || envelope.Output is null)
        {
            var failure = new InvalidDataException(envelope.Error ?? "The in-app ETW helper returned no filtered evidence.");
            ArmouryCaptureDiagnostics.Record(session.SessionId, "parent-result-rejected", failure, TryGetExitCode(session.HelperProcess));
            throw new ArmouryCaptureException(session.SessionId, failure.Message, failure);
        }
        await WaitForHelperExitAsync(session.HelperProcess, cancellationToken).ConfigureAwait(false);
        var output = envelope.Output;
        session.RecordAction("capture-stopped");

        var reports = output.Reports
            .Select((report, index) => AnalyseReport(index + 1, report))
            .ToList();
        var targetIdentityStable = await IsTargetIdentityStableAsync(session, cancellationToken).ConfigureAwait(false);
        var assessment = AssessCapture(session, output, reports, targetIdentityStable);
        var outputBytes = SerializeJson(output);
        var evidenceHash = Convert.ToHexString(SHA256.HashData(outputBytes)).ToLowerInvariant();
        var reportBytes = SerializeJson(new
        {
            schemaVersion = 5,
            actions = session.Actions,
            assessment,
            reports,
            schemaDiscovery = new UsbEtwSchemaDiscoveryReport(
                DiagnosticOnly: true,
                ContainsPayloadBytes: false,
                Complete: !output.SchemaDiscoveryLimitExceeded,
                output.SchemaShapes,
                output.MarkerShapes),
            etw = new
            {
                output.EnabledProviders,
                output.ObservedEventCount,
                output.EventsLost,
                output.OversizedEventCount,
                output.PayloadDecodeFailureCount,
                output.AmbiguousCandidateCount,
                output.DroppedMatchingReportCount,
                output.DecodedBinaryByteCount,
                output.AggregateLimitExceeded,
                output.SchemaDiscoveryLimitExceeded,
                retainedReportCount = reports.Count,
                retainedSchemaShapeCount = output.SchemaShapes.Count,
                retainedMarkerShapeCount = output.MarkerShapes.Count,
                fullDataBusTraceKeyword = $"0x{ArmouryEtwCaptureHelper.FullDataTraceKeywords:X}",
                privacy = "A system-wide USB ETW stream was inspected in memory. Schema discovery contains only bounded event/property/framing metadata grouped by action phase; it contains no generic payload bytes, payload hashes, raw ETL, timestamps, process IDs, device paths, pointers or scalar values.",
            },
        });
        var manifestBytes = SerializeJson(new
        {
            schemaVersion = 5,
            capturedAtUtc = DateTimeOffset.UtcNow,
            applicationVersion = GetApplicationVersion(),
            source = "Windows built-in USB ETW real-time FullDataBusTrace session",
            selectedAsusHid = session.Target,
            evidence = new
            {
                file = ArmouryEtwCapturePipe.ResultFileName,
                sha256 = evidenceHash,
                bytes = outputBytes.Length,
                rawSystemTraceWritten = false,
                hardwareUnlockEvidence = false,
            },
            schemaDiscovery = new
            {
                diagnosticOnly = true,
                containsPayloadBytes = false,
                complete = !output.SchemaDiscoveryLimitExceeded,
                phases = new
                {
                    baseline = 0,
                    m1A_m2B = 1,
                    m1X_m2Y = 2,
                    resetToDefault = 3,
                },
            },
            expectedProtocol = new
            {
                rearMappingPrefix = "5A D1 02 08 2C",
                diagnosticCommandPrefix = "D1 02 08 2C",
                minimumReportLength = AsusRearButtonProtocol.ReportLength,
                maximumReportLength = UsbEtwHidFeatureReportExtractor.MaximumWireReportLength,
                expectedReportId = "5A",
            },
            writeGates = new
            {
                customWritesApproved = ArmouryProtocolValidation.CustomWritesApproved,
                recoveryWritesApproved = ArmouryProtocolValidation.RecoveryWritesApproved,
            },
            assessment,
        });
        var readmeBytes = Encoding.UTF8.GetBytes(BuildReadme(
            reports.Count,
            output.SchemaShapes.Count,
            output.MarkerShapes.Count,
            assessment));

        var bundlePath = Path.Combine(
            session.Directory,
            $"ally-bindings-armoury-etw-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");
        var bundleSha256 = CreateBundle(bundlePath,
            (ArmouryEtwCapturePipe.ResultFileName, outputBytes),
            ("feature-reports.json", reportBytes),
            ("manifest.json", manifestBytes),
            ("README.txt", readmeBytes));
        session.Dispose();
        ArmouryCaptureDiagnostics.Delete(session.SessionId);
        return new(
            bundlePath,
            reports.Count,
            reports.Count(report => report.IsStructurallyValidRearMapping),
            assessment.IsConclusive,
            assessment.Reasons,
            bundleSha256);
    }

    private static async Task<EtwPipeConnection> WaitForReadyAsync(
        Guid sessionId,
        Process helper,
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CaptureStartTimeout);
        try
        {
            var connectTask = pipe.WaitForConnectionAsync(timeout.Token);
            var exitTask = helper.WaitForExitAsync(timeout.Token);
            var completed = await Task.WhenAny(connectTask, exitTask).ConfigureAwait(false);
            if (completed == exitTask)
            {
                await exitTask.ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"The elevated in-app ETW helper exited before connecting (exit code {helper.ExitCode}).");
            }
            await connectTask.ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientProcessId) ||
                clientProcessId != (uint)helper.Id)
            {
                throw new InvalidOperationException(
                    "The ETW named-pipe client was not the elevated Ally Bindings helper process.");
            }

            var reader = new BoundedTextLineReader(
                new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true));
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };
            var envelope = await ReadEnvelopeAsync(
                sessionId,
                helper,
                reader,
                CaptureStartTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!envelope.Type.Equals("ready", StringComparison.Ordinal) || envelope.Ready is null)
            {
                reader.Dispose();
                writer.Dispose();
                var failure = new InvalidOperationException(envelope.Error ?? "The in-app ETW helper did not become ready.");
                ArmouryCaptureDiagnostics.Record(sessionId, "parent-ready-rejected", failure, TryGetExitCode(helper));
                throw new ArmouryCaptureException(sessionId, failure.Message, failure);
            }
            return new(reader, writer, envelope.Ready);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            StopHelper(helper);
            throw new TimeoutException("Windows did not start the in-app USB ETW session within 30 seconds.");
        }
    }

    private static async Task<EtwPipeEnvelope> ReadEnvelopeAsync(
        Guid sessionId,
        Process helper,
        BoundedTextLineReader reader,
        TimeSpan timeoutDuration,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutDuration);
        string? line;
        try
        {
            line = await reader.ReadLineAsync(MaximumPipeEnvelopeCharacters, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The in-app USB ETW helper stopped responding.");
        }
        if (line is null)
        {
            var helperExitCode = TryGetExitCode(helper);
            var failure = new InvalidDataException(
                $"The in-app USB ETW helper disconnected without a result{FormatExitCode(helperExitCode)}.");
            ArmouryCaptureDiagnostics.Record(sessionId, "parent-helper-disconnected", failure, helperExitCode);
            throw new ArmouryCaptureException(sessionId, failure.Message, failure);
        }
        return JsonSerializer.Deserialize<EtwPipeEnvelope>(line, JsonOptions)
            ?? throw new InvalidDataException("The in-app USB ETW helper returned an empty message.");
    }

    private static async Task WaitForHelperExitAsync(
        Process helper,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CaptureStopTimeout);
        try
        {
            await helper.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            StopHelper(helper);
            throw new TimeoutException("The in-app USB ETW helper did not stop within 30 seconds.");
        }
        if (helper.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The in-app USB ETW helper failed while completing capture (exit code {helper.ExitCode}).");
        }
    }

    private static CapturedReportAnalysis AnalyseReport(int index, UsbEtwFeatureReport report)
    {
        var payload = report.Report;
        var structurallyValid =
            payload.Length >= AsusRearButtonProtocol.ReportLength &&
            payload.Length <= UsbEtwHidFeatureReportExtractor.MaximumWireReportLength &&
            payload.AsSpan().StartsWith(new byte[] { 0x5A, 0xD1, 0x02, 0x08, 0x2C });
        return new(
            index,
            report.Timestamp,
            report.PerformanceCounterTimestamp,
            report.ProviderName,
            report.EventName,
            report.EventId,
            report.SourceField,
            report.SourceOffset,
            Convert.ToHexString(payload),
            report.Sha256,
            structurallyValid,
            structurallyValid && AsusRearButtonProtocol.MatchesWireReport(payload, AsusRearButtonProtocol.BuildMappingReport(ControllerButton.A, ControllerButton.B)),
            structurallyValid && AsusRearButtonProtocol.MatchesWireReport(payload, AsusRearButtonProtocol.BuildMappingReport(ControllerButton.X, ControllerButton.Y)),
            structurallyValid && AsusRearButtonProtocol.MatchesWireReport(payload, AsusRearButtonProtocol.BuildNativeResetReport()));
    }

    private static CaptureAssessment AssessCapture(
        ArmouryCaptureSession session,
        EtwCaptureOutput output,
        IReadOnlyList<CapturedReportAnalysis> reports,
        bool targetIdentityStable)
    {
        static DateTimeOffset MonotonicTimestamp(long qpc) =>
            DateTimeOffset.UnixEpoch.AddSeconds((double)qpc / Stopwatch.Frequency);
        DateTimeOffset Marker(string name) => MonotonicTimestamp(
            session.Actions.Single(action => action.Action == name).PerformanceCounterTimestamp);
        var evidence = reports.Select(report => new ArmouryCaptureReportEvidence(
            MonotonicTimestamp(report.PerformanceCounterTimestamp),
            report.IsStructurallyValidRearMapping,
            report.MatchesRequestedM1A_M2B,
            report.MatchesRequestedM1X_M2Y,
            report.MatchesExpectedNativeReset)).ToList();
        var windows = new[]
        {
            new ArmouryCaptureStepWindow(
                "M1=A / M2=B",
                Marker("step-started-m1-a-m2-b"),
                Marker("armoury-applied-m1-a-m2-b"),
                ArmouryCaptureExpectedReport.M1A_M2B),
            new ArmouryCaptureStepWindow(
                "M1=X / M2=Y",
                Marker("step-started-m1-x-m2-y"),
                Marker("armoury-applied-m1-x-m2-y"),
                ArmouryCaptureExpectedReport.M1X_M2Y),
            new ArmouryCaptureStepWindow(
                "Reset to Default",
                Marker("step-started-reset-to-default"),
                Marker("armoury-reset-m1-m2-to-default"),
                ArmouryCaptureExpectedReport.NativeReset),
        };
        var captureFailure =
            output.EventsLost != 0 ||
            output.OversizedEventCount != 0 ||
            output.PayloadDecodeFailureCount != 0 ||
            output.AmbiguousCandidateCount != 0 ||
            output.DroppedMatchingReportCount != 0 ||
            output.AggregateLimitExceeded ||
            output.SchemaDiscoveryLimitExceeded;
        var validation = ArmouryCaptureSequenceValidator.Validate(
            evidence,
            windows,
            captureFailure ? 1 : 0,
            // Candidate payloads cannot become unlock evidence until physical
            // Ally validation binds the Windows-build-specific ETW schema to
            // the confirmed HID interface and SET_REPORT setup packet.
            captureScopeVerified: false,
            targetIdentityStable);
        return new(
            validation.IsConclusive,
            validation.FirstMappingMatched,
            validation.SecondMappingMatched,
            validation.NativeResetMatched,
            validation.Reasons);
    }

    private async Task<bool> IsTargetIdentityStableAsync(
        ArmouryCaptureSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await DiscoverTargetAsync(cancellationToken).ConfigureAwait(false);
            return IsSameTarget(session.Target, current);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameTarget(ArmouryCaptureTarget expected, ArmouryCaptureTarget actual) =>
        string.Equals(expected.Model, actual.Model, StringComparison.OrdinalIgnoreCase) &&
        expected.DeviceIds.Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(actual.DeviceIds.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static int? TryGetExitCode(Process? helper)
    {
        if (helper is null) return null;
        try
        {
            return helper.HasExited ? helper.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatExitCode(int? exitCode) =>
        exitCode.HasValue ? $" (exit code {exitCode.Value})" : string.Empty;

    private static void StopHelper(Process helper)
    {
        try
        {
            if (!helper.HasExited && !helper.WaitForExit(5_000))
            {
                helper.Kill(entireProcessTree: true);
                helper.WaitForExit(5_000);
            }
        }
        catch
        {
            try
            {
                if (!helper.HasExited) helper.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort; TraceEventSession also stops its session on helper disposal/process exit.
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint clientProcessId);

    private static byte[] SerializeJson(object value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static string GetApplicationVersion()
    {
        var assembly = typeof(ArmouryCaptureService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }


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
                    using var entryStream = entry.Open();
                    entryStream.Write(artifact.Content);
                }
            }

            string sha256;
            using (var bundleStream = File.OpenRead(temporaryPath))
            {
                sha256 = Convert.ToHexString(SHA256.HashData(bundleStream)).ToLowerInvariant();
            }
            File.Move(temporaryPath, bundlePath);
            return sha256;
        }
        catch (Exception captureFailure)
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    $"Capture failed and the incomplete private artifact could not be removed: {temporaryPath}",
                    captureFailure,
                    cleanupFailure);
            }
            throw;
        }
    }

    private static string BuildReadme(
        int reportCount,
        int schemaShapeCount,
        int markerShapeCount,
        CaptureAssessment assessment) =>
        $"Ally Bindings integrated Windows USB ETW Armoury capture{Environment.NewLine}" +
        $"Retained ASUS rear-mapping report candidates: {reportCount}{Environment.NewLine}{Environment.NewLine}" +
        $"Retained metadata-only ETW property shapes: {schemaShapeCount}{Environment.NewLine}" +
        $"Retained metadata-only ASUS marker shapes: {markerShapeCount}{Environment.NewLine}{Environment.NewLine}" +
        $"Assessment: REVIEW REQUIRED — NOT HARDWARE UNLOCK EVIDENCE{Environment.NewLine}" +
        (assessment.Reasons.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, assessment.Reasons.Select(reason => $"- {reason}")) + Environment.NewLine) +
        "Windows' built-in USB ETW providers were consumed in real time with FullDataBusTrace. No USBPcap/Wireshark driver, raw ETL, or raw PCAP was written. Schema discovery contains only bounded event/property/framing metadata grouped by action phase: no generic payload bytes, payload hashes, raw timestamps, process IDs, device paths, pointers or scalar values. Discovery metadata is never hardware-unlock evidence. Exact target-device SET_REPORT scope and vectors still require physical review. Ally Bindings sent no HID write and cannot clear recovery state from this capture. Hardware writes remain source locked.";

}

internal sealed class ArmouryCaptureSession(
    Guid sessionId,
    Process helperProcess,
    NamedPipeServerStream pipe,
    BoundedTextLineReader pipeReader,
    StreamWriter pipeWriter,
    string directory,
    ArmouryCaptureTarget target,
    IReadOnlyList<string> enabledProviders) : IDisposable
{
    private int _disposed;

    public Guid SessionId { get; } = sessionId;
    public Process HelperProcess { get; } = helperProcess;
    public NamedPipeServerStream Pipe { get; } = pipe;
    public BoundedTextLineReader PipeReader { get; } = pipeReader;
    public StreamWriter PipeWriter { get; } = pipeWriter;
    public string Directory { get; } = directory;
    public ArmouryCaptureTarget Target { get; } = target;
    public IReadOnlyList<string> EnabledProviders { get; } = enabledProviders;
    public List<CaptureActionMarker> Actions { get; } = [];

    public void RecordAction(string action, long? qpcOverride = null)
    {
        var occurredAtUtc = DateTimeOffset.UtcNow;
        var qpc = qpcOverride ?? Stopwatch.GetTimestamp();
        if (qpc <= 0) throw new ArgumentOutOfRangeException(nameof(qpcOverride));
        Actions.Add(new(occurredAtUtc, qpc, action));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        TryDispose(PipeWriter);
        TryDispose(PipeReader);
        TryDispose(Pipe);
        TryDispose(HelperProcess);
    }

    public void CancelAndDelete()
    {
        Exception? cleanupFailure = null;
        try
        {
            PipeWriter.WriteLine("cancel");
            if (!HelperProcess.HasExited && !HelperProcess.WaitForExit(5_000))
            {
                HelperProcess.Kill(entireProcessTree: true);
                HelperProcess.WaitForExit(5_000);
            }
        }
        catch
        {
            try
            {
                if (!HelperProcess.HasExited) HelperProcess.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort; cancelled evidence is never accepted.
            }
        }
        finally
        {
            Dispose();
            ArmouryCaptureDiagnostics.Delete(SessionId);
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }
        }
        if (cleanupFailure is not null)
        {
            throw new IOException(
                $"Cancelled capture data could not be removed and remains at: {Directory}",
                cleanupFailure);
        }
    }

    private static void TryDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch
        {
            // Lifecycle cleanup is best effort; capture evidence is gated separately.
        }
    }
}

internal sealed record EtwPipeConnection(
    BoundedTextLineReader Reader,
    StreamWriter Writer,
    EtwCaptureReady Ready);

internal sealed record ArmouryCaptureTarget(string Model, IReadOnlyList<string> DeviceIds);
internal sealed record CaptureActionMarker(
    DateTimeOffset TimestampUtc,
    long PerformanceCounterTimestamp,
    string Action);
internal sealed record ArmouryCaptureResult(
    string BundlePath,
    int FeatureReportCount,
    int RearMappingReportCount,
    bool IsConclusive,
    IReadOnlyList<string> AssessmentReasons,
    string BundleSha256);
internal sealed record CapturedReportAnalysis(
    int Index,
    DateTimeOffset Timestamp,
    long PerformanceCounterTimestamp,
    string ProviderName,
    string EventName,
    int EventId,
    string SourceField,
    int SourceOffset,
    string PayloadHex,
    string PayloadSha256,
    bool IsStructurallyValidRearMapping,
    bool MatchesRequestedM1A_M2B,
    bool MatchesRequestedM1X_M2Y,
    bool MatchesExpectedNativeReset);
internal sealed record CaptureAssessment(
    bool IsConclusive,
    bool FirstMappingMatched,
    bool SecondMappingMatched,
    bool ResetMatched,
    IReadOnlyList<string> Reasons);
