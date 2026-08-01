using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AllyBindings.Core;

namespace AllyBindings.Windows;

internal sealed partial class ArmouryCaptureService
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CaptureStartTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<ArmouryCaptureSession> StartAsync(CancellationToken cancellationToken = default)
    {
        var toolPath = FindUsbPcapCommand()
            ?? throw new InvalidOperationException(
                "USBPcap is not installed. Install Wireshark with the USBPcap component, reboot if its installer asks, then retry. Ally Bindings will never install a kernel capture driver automatically.");
        var directory = CreateCaptureDirectory();
        var enumerationPath = Path.Combine(directory, "usbpcap-selected-device.txt");
        var target = await FindTargetAsync(toolPath, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            enumerationPath,
            $"Control device: {target.ControlDevice}{Environment.NewLine}" +
            $"USB address: {target.Address}{Environment.NewLine}" +
            $"Matched descriptions:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", target.Descriptions)}{Environment.NewLine}",
            cancellationToken).ConfigureAwait(false);

        var pcapPath = Path.Combine(directory, "armoury-usb-device-only.pcap");
        var scriptPath = Path.Combine(directory, "run-passive-capture.cmd");
        var ownerReadyPath = Path.Combine(directory, "ally-bindings-owns-capture.signal");
        await File.WriteAllTextAsync(
            scriptPath,
            BuildCaptureScript(toolPath, target, pcapPath, ownerReadyPath),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            Arguments = $"/d /c \"\"{scriptPath}\"\"",
            WorkingDirectory = directory,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        }) ?? throw new InvalidOperationException("Windows did not start the USBPcap capture console.");

        CaptureProcessJob? processJob = null;
        try
        {
            processJob = CaptureProcessJob.Assign(process);
            await File.WriteAllTextAsync(ownerReadyPath, "owned", cancellationToken).ConfigureAwait(false);
            await WaitForCaptureStartAsync(process, pcapPath, cancellationToken).ConfigureAwait(false);
            var session = new ArmouryCaptureSession(process, processJob, directory, pcapPath, target, toolPath);
            session.MarkAction("capture-started");
            return session;
        }
        catch
        {
            processJob?.Dispose();
            process.Dispose();
            throw;
        }
    }

    public async Task<ArmouryCaptureResult> CompleteAsync(
        ArmouryCaptureSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.Process.HasExited)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await session.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "The capture is still running. Return to the USBPcap console and press q, then retry completion.");
            }
        }
        if (session.Process.ExitCode != 0)
        {
            throw new InvalidOperationException($"USBPcap exited with code {session.Process.ExitCode}; no capture is being claimed as valid.");
        }

        session.MarkAction("capture-stopped");
        UsbPcapParseResult parsed;
        await using (var stream = new FileStream(
            session.PcapPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true))
        {
            parsed = UsbPcapHidFeatureReportParser.Parse(stream);
        }

        var featureReports = parsed.Reports
            .Where(report => report.ReportId == AsusRearButtonProtocol.FeatureReportId)
            .ToList();
        var analyses = featureReports
            .Select((report, index) => AnalyseReport(index + 1, report))
            .ToList();
        var targetIdentityStable = await IsTargetIdentityStableAsync(session, cancellationToken).ConfigureAwait(false);
        var assessment = AssessCapture(session, parsed, analyses, targetIdentityStable);
        var pcapHash = await ComputeSha256Async(session.PcapPath, cancellationToken).ConfigureAwait(false);
        var reportPath = Path.Combine(session.Directory, "feature-reports.json");
        var manifestPath = Path.Combine(session.Directory, "manifest.json");
        var instructionsPath = Path.Combine(session.Directory, "README.txt");

        await WriteJsonAsync(reportPath, new
        {
            schemaVersion = 1,
            actions = session.Actions,
            assessment,
            reports = analyses,
            parser = new
            {
                parsed.RecordCount,
                parsed.TruncatedRecordCount,
                allHidFeatureReportCount = parsed.Reports.Count,
                report5ACount = featureReports.Count,
            },
        }, cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(manifestPath, new
        {
            schemaVersion = 1,
            capturedAtUtc = DateTimeOffset.UtcNow,
            applicationVersion = typeof(ArmouryCaptureService).Assembly.GetName().Version?.ToString(),
            source = "USBPcap device-address-filtered passive capture",
            selectedUsbDevice = new
            {
                session.Target.ControlDevice,
                session.Target.Address,
                session.Target.Descriptions,
            },
            usbPcapVersion = FileVersionInfo.GetVersionInfo(session.ToolPath).FileVersion,
            rawCapture = new
            {
                file = Path.GetFileName(session.PcapPath),
                sha256 = pcapHash,
                bytes = new FileInfo(session.PcapPath).Length,
            },
            expectedProtocol = new
            {
                hidSetup = "21 09 5A 03 <interface> <length>",
                rearMappingPrefix = "5A D1 02 08 2C",
                expectedReportId = "5A",
            },
            writeGates = new
            {
                customWritesApproved = ArmouryProtocolValidation.CustomWritesApproved,
                recoveryWritesApproved = ArmouryProtocolValidation.RecoveryWritesApproved,
            },
            assessment,
        }, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(instructionsPath, BuildReadme(featureReports.Count, assessment), cancellationToken).ConfigureAwait(false);

        var bundlePath = Path.Combine(session.Directory, $"ally-bindings-armoury-capture-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");
        CreateBundle(bundlePath, session.PcapPath, reportPath, manifestPath, instructionsPath);
        session.Dispose();
        return new(
            bundlePath,
            session.PcapPath,
            featureReports.Count,
            analyses.Count(analysis => analysis.IsStructurallyValidRearMapping),
            assessment.IsConclusive,
            assessment.Reasons,
            pcapHash);
    }

    private static async Task<UsbPcapTarget> FindTargetAsync(
        string toolPath,
        CancellationToken cancellationToken)
    {
        var interfaceOutput = await RunToolAsync(toolPath, ["--extcap-interfaces"], cancellationToken).ConfigureAwait(false);
        var controlDevices = InterfaceRegex().Matches(interfaceOutput)
            .Select(match => match.Groups["value"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (controlDevices.Count == 0)
        {
            throw new InvalidOperationException("USBPcap is installed, but it exposed no capture control devices.");
        }

        var targets = new List<UsbPcapTarget>();
        foreach (var controlDevice in controlDevices)
        {
            var output = await RunToolAsync(
                toolPath,
                ["--extcap-interface", controlDevice, "--extcap-config"],
                cancellationToken).ConfigureAwait(false);
            var groups = DeviceRegex().Matches(output)
                .Select(match => new
                {
                    Address = ushort.Parse(match.Groups["address"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Description = match.Groups["display"].Value,
                })
                .GroupBy(item => item.Address);
            foreach (var group in groups)
            {
                var descriptions = group.Select(item => item.Description).Distinct().ToArray();
                if (descriptions.Any(IsAsusNKeyDescription))
                {
                    targets.Add(new(controlDevice, group.Key, descriptions));
                }
            }
        }

        var exactTargets = targets
            .Where(target => target.Descriptions.Any(description =>
                description.Contains("ASUS N-KEY", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (exactTargets.Count == 1)
        {
            return exactTargets[0];
        }
        if (targets.Count == 1)
        {
            return targets[0];
        }
        if (targets.Count == 0)
        {
            throw new InvalidOperationException(
                "USBPcap could not identify an ASUS N-KEY device. No broad root-hub capture was started; this is a privacy fail-closed condition.");
        }
        throw new InvalidOperationException(
            $"USBPcap found {targets.Count} possible ASUS N-KEY USB devices. No capture was started because the device filter was ambiguous.");
    }

    private static bool IsAsusNKeyDescription(string description)
    {
        var hasAsusIdentity =
            description.Contains("ASUS", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("ROG ALLY", StringComparison.OrdinalIgnoreCase);
        var hasNKeyIdentity =
            description.Contains("N-KEY", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("N KEY", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("N_KEY", StringComparison.OrdinalIgnoreCase);
        return hasAsusIdentity && hasNKeyIdentity;
    }

    private static async Task<string> RunToolAsync(
        string toolPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = toolPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (!process.Start())
        {
            throw new InvalidOperationException("Windows did not start USBPcapCMD for device discovery.");
        }
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ToolTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("USBPcap device discovery timed out safely; no capture was started.");
        }
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"USBPcap device discovery failed with code {process.ExitCode}: {error.Trim()}");
        }
        return output;
    }

    private static async Task WaitForCaptureStartAsync(
        Process process,
        string pcapPath,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + CaptureStartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"USBPcap exited before capture began (code {process.ExitCode}).");
            }
            if (File.Exists(pcapPath) && new FileInfo(pcapPath).Length >= 24)
            {
                return;
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException(
            "USBPcap did not create a capture within 30 seconds. If a UAC prompt is open, reject it and retry when ready.");
    }

    private static string? FindUsbPcapCommand()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new[]
        {
            Path.Combine(programFiles, "USBPcap", "USBPcapCMD.exe"),
            Path.Combine(programFiles, "Wireshark", "extcap", "USBPcapCMD.exe"),
            Path.Combine(programFilesX86, "USBPcap", "USBPcapCMD.exe"),
            Path.Combine(programFilesX86, "Wireshark", "extcap", "USBPcapCMD.exe"),
        }.Where(path => !string.IsNullOrWhiteSpace(path));
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, "USBPcapCMD.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string CreateCaptureDirectory()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AllyBindings",
            "captures");
        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, $"armoury-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string BuildCaptureScript(
        string toolPath,
        UsbPcapTarget target,
        string pcapPath,
        string ownerReadyPath)
    {
        ValidateCmdPath(toolPath);
        ValidateCmdPath(pcapPath);
        ValidateCmdPath(target.ControlDevice);
        ValidateCmdPath(ownerReadyPath);
        return "@echo off\r\n" +
               "title Ally Bindings - passive Armoury capture - press q to stop\r\n" +
               "echo Passive device-only USB capture. Ally Bindings sends no controller reports.\r\n" +
               "echo Keep this window open while following the prompts, then press q to stop.\r\n" +
               ":wait_for_ally_bindings_owner\r\n" +
               $"if not exist \"{ownerReadyPath}\" (\r\n" +
               "  ping 127.0.0.1 -n 2 >nul\r\n" +
               "  goto wait_for_ally_bindings_owner\r\n" +
               ")\r\n" +
               $"start \"\" /wait \"{toolPath}\" -d \"{target.ControlDevice}\" --devices {target.Address} --inject-descriptors -s 65535 -o \"{pcapPath}\"\r\n" +
               "set \"captureExit=%ERRORLEVEL%\"\r\n" +
               "exit /b %captureExit%\r\n";
    }

    private static void ValidateCmdPath(string value)
    {
        if (value.IndexOfAny(['\r', '\n', '"', '&', '|', '<', '>']) >= 0)
        {
            throw new InvalidOperationException("A capture path contained characters that are unsafe for a Windows command script.");
        }
    }

    private static CapturedReportAnalysis AnalyseReport(int index, CapturedHidFeatureReport report)
    {
        var payload = Convert.FromHexString(report.PayloadHex);
        var structurallyValid = report.PayloadReportIdMatches &&
            report.LengthMatchesDeclared &&
            payload.Length >= AsusRearButtonProtocol.ReportLength &&
            payload.AsSpan().StartsWith(new byte[] { 0x5A, 0xD1, 0x02, 0x08, 0x2C });
        return new(
            index,
            report.Timestamp,
            report.Bus,
            report.Device,
            report.InterfaceNumber,
            report.DeclaredLength,
            report.CapturedLength,
            report.LengthMatchesDeclared,
            report.PayloadReportIdMatches,
            report.SetupHex,
            report.PayloadHex,
            structurallyValid,
            structurallyValid && AsusRearButtonProtocol.MatchesWireReport(payload, AsusRearButtonProtocol.BuildMappingReport(ControllerButton.A, ControllerButton.B)),
            structurallyValid && AsusRearButtonProtocol.MatchesWireReport(payload, AsusRearButtonProtocol.BuildMappingReport(ControllerButton.X, ControllerButton.Y)),
            structurallyValid && AsusRearButtonProtocol.MatchesWireReport(payload, AsusRearButtonProtocol.BuildNativeResetReport()));
    }

    private static CaptureAssessment AssessCapture(
        ArmouryCaptureSession session,
        UsbPcapParseResult parsed,
        IReadOnlyList<CapturedReportAnalysis> reports,
        bool targetIdentityStable)
    {
        var reasons = new List<string>();
        DateTimeOffset Marker(string name) => session.Actions.Single(action => action.Action == name).TimestampUtc;
        var start = Marker("capture-started");
        var first = Marker("armoury-applied-m1-a-m2-b");
        var second = Marker("armoury-applied-m1-x-m2-y");
        var reset = Marker("armoury-reset-m1-m2-to-default");

        var firstMatch = reports.Any(report => report.Timestamp >= start && report.Timestamp <= first && report.MatchesRequestedM1A_M2B);
        var secondMatch = reports.Any(report => report.Timestamp > first && report.Timestamp <= second && report.MatchesRequestedM1X_M2Y);
        var resetMatch = reports.Any(report => report.Timestamp > second && report.Timestamp <= reset && report.MatchesExpectedNativeReset);
        if (!firstMatch) reasons.Add("No exact M1=A / M2=B report was captured in its action window.");
        if (!secondMatch) reasons.Add("No exact M1=X / M2=Y report was captured in its action window.");
        if (!resetMatch) reasons.Add("No exact native-reset report was captured in its action window.");
        if (parsed.TruncatedRecordCount != 0) reasons.Add($"The PCAP contains {parsed.TruncatedRecordCount} truncated record(s).");
        if (reports.Any(report => report.Device != session.Target.Address)) reasons.Add("A parsed report did not match the confirmed USB device address.");
        if (!targetIdentityStable) reasons.Add("The selected ASUS USB identity changed or disappeared before post-capture verification.");

        return new(reasons.Count == 0, firstMatch, secondMatch, resetMatch, reasons);
    }

    private static async Task<bool> IsTargetIdentityStableAsync(
        ArmouryCaptureSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await FindTargetAsync(session.ToolPath, cancellationToken).ConfigureAwait(false);
            return current.Address == session.Target.Address &&
                string.Equals(current.ControlDevice, session.Target.ControlDevice, StringComparison.OrdinalIgnoreCase) &&
                current.Descriptions.Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(session.Target.Descriptions.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WriteJsonAsync(
        string path,
        object value,
        CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(value, JsonOptions),
            cancellationToken).ConfigureAwait(false);

    private static void CreateBundle(string bundlePath, params string[] files)
    {
        using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            archive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
        }
    }

    private static string BuildReadme(int reportCount, CaptureAssessment assessment) =>
        $"Ally Bindings passive Armoury Crate capture{Environment.NewLine}" +
        $"Extracted ASUS feature-report 0x5A count: {reportCount}{Environment.NewLine}{Environment.NewLine}" +
        $"Assessment: {(assessment.IsConclusive ? "CONCLUSIVE" : "INCONCLUSIVE")}{Environment.NewLine}" +
        (assessment.Reasons.Count == 0 ? string.Empty : string.Join(Environment.NewLine, assessment.Reasons.Select(reason => $"- {reason}")) + Environment.NewLine) +
        "The capture was filtered to the selected ASUS N-KEY USB device address. It may still contain traffic from other interfaces of that one composite device, so treat the ZIP as private diagnostic data. No broad root-hub capture was used. Ally Bindings did not issue any HID write during this workflow; only Armoury Crate actions requested by the prompts changed mappings. Hardware writes remain source locked pending analysis.";

    [GeneratedRegex(@"interface \{value=(?<value>[^}]+)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InterfaceRegex();

    [GeneratedRegex(@"value \{arg=\d+\}\{value=(?<address>\d+)(?:_\d+)?\}\{display=(?<display>[^}]*)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceRegex();
}

internal sealed class ArmouryCaptureSession(
    Process process,
    CaptureProcessJob processJob,
    string directory,
    string pcapPath,
    UsbPcapTarget target,
    string toolPath) : IDisposable
{
    public Process Process { get; } = process;
    public string Directory { get; } = directory;
    public string PcapPath { get; } = pcapPath;
    public UsbPcapTarget Target { get; } = target;
    public string ToolPath { get; } = toolPath;
    public List<CaptureActionMarker> Actions { get; } = [];

    public void MarkAction(string action) => Actions.Add(new(DateTimeOffset.UtcNow, action));

    public void Dispose()
    {
        processJob.Dispose();
        Process.Dispose();
    }
}

internal sealed class CaptureProcessJob : IDisposable
{
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private IntPtr _handle;

    private CaptureProcessJob(IntPtr handle) => _handle = handle;

    public static CaptureProcessJob Assign(Process process)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Windows could not create the capture lifecycle job (error {Marshal.GetLastWin32Error()}).");
        }

        var job = new CaptureProcessJob(handle);
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        if (!SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformationClass,
                ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()) ||
            !AssignProcessToJobObject(handle, process.Handle))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new InvalidOperationException($"Windows could not bind USBPcap to the Ally Bindings lifecycle (error {error}). Capture was not started.");
        }
        return job;
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        uint informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal sealed record UsbPcapTarget(string ControlDevice, ushort Address, IReadOnlyList<string> Descriptions);
internal sealed record CaptureActionMarker(DateTimeOffset TimestampUtc, string Action);
internal sealed record ArmouryCaptureResult(
    string BundlePath,
    string RawCapturePath,
    int FeatureReportCount,
    int RearMappingReportCount,
    bool IsConclusive,
    IReadOnlyList<string> AssessmentReasons,
    string RawCaptureSha256);
internal sealed record CapturedReportAnalysis(
    int Index,
    DateTimeOffset Timestamp,
    ushort Bus,
    ushort Device,
    ushort InterfaceNumber,
    ushort DeclaredLength,
    int CapturedLength,
    bool LengthMatchesDeclared,
    bool PayloadReportIdMatches,
    string SetupHex,
    string PayloadHex,
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
