using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AllyBindings.Core;
using Microsoft.Win32.SafeHandles;

namespace AllyBindings.Windows;

internal static class ArmouryTapCaptureHelper
{
    internal const string HelperArgument = "--armoury-tap-capture-helper";
    internal const string NativeResourceName = "AllyBindings.Windows.Native.AllyBindings.ArmouryTap.dll";
    internal const string TapUnavailableErrorCode = "tap-unavailable";
    internal const string TeardownUnconfirmedErrorCode = "teardown-unconfirmed";
    private static readonly TimeSpan MaximumCaptureDuration = TimeSpan.FromMinutes(10);
    private const int MaximumCandidateProcesses = ArmouryTapProtocol.MaximumCandidateProcesses;
    private static readonly JsonSerializerOptions JsonOptions = new();

    public static bool TryParseArguments(IReadOnlyList<string> args, out Guid sessionId, out int parentProcessId)
    {
        sessionId = Guid.Empty;
        parentProcessId = 0;
        return args.Count == 3 && args[0].Equals(HelperArgument, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(args[1], "D", out sessionId) && int.TryParse(args[2], out parentProcessId) &&
            parentProcessId > 0;
    }

    public static async Task<int> RunAsync(Guid sessionId, int parentProcessId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return 1;
        await using var pipe = new NamedPipeClientStream(".", ArmouryEtwCapturePipe.GetPipeName(sessionId),
            PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(15_000, cancellationToken).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != (uint)parentProcessId)
                throw new InvalidOperationException("The tap pipe server was not the expected Ally Bindings parent.");
            VerifyParentExecutableIdentity(parentProcessId);
            using var reader = new BoundedTextLineReader(new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true));
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            return await CaptureAsync(sessionId, parentProcessId, reader, writer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (pipe.IsConnected)
            {
                try
                {
                    await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                    var errorCode = ex switch
                    {
                        TapUnavailableException => TapUnavailableErrorCode,
                        TapTeardownUnconfirmedException => TeardownUnconfirmedErrorCode,
                        _ => null,
                    };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(
                        new EtwPipeEnvelope("error", Error: ex.Message, ErrorCode: errorCode), JsonOptions));
                }
                catch { }
            }
            return 1;
        }
    }

    private static async Task<int> CaptureAsync(Guid sessionId, int parentProcessId,
        BoundedTextLineReader reader, StreamWriter writer,
        CancellationToken cancellationToken)
    {
        var extractionDirectory = CreatePrivateExtractionDirectory(sessionId);
        var dllPath = Path.Combine(extractionDirectory, "AllyBindings.ArmouryTap.dll");
        var targets = new List<TappedProcess>();
        var candidates = new List<VerifiedProcess>();
        SafeAccessTokenHandle? unelevatedToken = null;
        var phaseWindows = new Dictionary<int, PhaseWindow>();
        FileStream? dllLock = null;
        bool cleanupCompleted = false;
        async Task CleanupAsync()
        {
            if (cleanupCompleted) return;
            List<Exception> failures = [];
            for (var attempt = 0; attempt < 2; attempt++)
            {
                failures.Clear();
                foreach (var target in targets)
                {
                    try { await target.DetachAsync().ConfigureAwait(false); }
                    catch (Exception ex) { failures.Add(ex); }
                }
                if (failures.Count == 0) break;
                if (attempt == 0) await Task.Delay(100).ConfigureAwait(false);
            }
            if (failures.Count != 0)
                throw new TapTeardownUnconfirmedException(
                    "One or more native hooks could not be positively removed after a bounded retry; image and DLL locks remain held.",
                    new AggregateException(failures));

            foreach (var target in targets)
            {
                try { target.Dispose(); }
                catch (Exception ex) { failures.Add(ex); }
            }
            foreach (var candidate in candidates)
            {
                try { candidate.ImageLock.Dispose(); }
                catch (Exception ex) { failures.Add(ex); }
                try { candidate.LifecycleHandle.Dispose(); }
                catch (Exception ex) { failures.Add(ex); }
            }
            try { unelevatedToken?.Dispose(); }
            catch (Exception ex) { failures.Add(ex); }
            try { dllLock?.Dispose(); }
            catch (Exception ex) { failures.Add(ex); }
            try { DeleteExtractionDirectory(extractionDirectory); }
            catch (Exception ex) { failures.Add(ex); }
            if (failures.Count != 0)
                throw new TapTeardownUnconfirmedException(
                    "One or more file locks or temporary files could not be positively removed.",
                    new AggregateException(failures));
            cleanupCompleted = true;
        }
        try
        {
            var expectedDllHash = ExtractNativeDll(dllPath);
            dllLock = LockAndVerifyNativeDll(dllPath, expectedDllHash);
            unelevatedToken = OpenParentImpersonationToken(parentProcessId);
            candidates = DiscoverSignedCandidates(unelevatedToken, out var candidateDiagnostic);
            if (candidates.Count == 0)
                throw new TapUnavailableException(
                    $"No injectable ASUS-signed Armoury process was found for the user-mode tap. {candidateDiagnostic}");
            if (candidates.Count > MaximumCandidateProcesses)
                throw new InvalidOperationException($"Found {candidates.Count} ASUS Armoury candidates; the safe limit is {MaximumCandidateProcesses}.");

            var attachmentRejections = await CandidateAttachmentCoordinator.AttachAvailableAsync(
                candidates,
                targets,
                candidate => candidate.ExactName,
                (candidate, token) => TappedProcess.AttachAsync(sessionId, candidate, dllPath, token),
                ex => ex is TapTeardownUnconfirmedException,
                DescribeSafeAttachRejection,
                cancellationToken).ConfigureAwait(false);
            if (targets.Count == 0)
            {
                var rejected = attachmentRejections.Count == 0
                    ? "No candidate reached the attachment stage."
                    : "Attachment outcomes: " + string.Join("; ", attachmentRejections
                        .Select(item => $"{item.CandidateName}=[{item.Reason}]")) + ".";
                throw new TapUnavailableException(
                    $"No verified Armoury process accepted the native tap. {candidateDiagnostic} {rejected}");
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(new EtwPipeEnvelope("ready", Ready: new(
                [new("AllyBindings native user-mode HID write tap", Guid.Empty, 0)])), JsonOptions));

            string? command = null;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lifetime.CancelAfter(MaximumCaptureDuration);
            while (true)
            {
                command = await reader.ReadLineAsync(UsbEtwCapturePhaseCommand.MaximumCommandCharacters, lifetime.Token)
                    .ConfigureAwait(false);
                if (command is null or "stop" or "cancel") break;
                if (!UsbEtwCapturePhaseCommand.TryParse(command, out var phase, out var transition))
                    throw new InvalidDataException("The tap helper received an invalid phase command.");
                var boundary = Stopwatch.GetTimestamp();
                if (transition == UsbEtwCapturePhaseTransition.Start)
                {
                    if (phaseWindows.ContainsKey(phase))
                        throw new InvalidDataException($"Capture phase {phase} started more than once.");
                    phaseWindows.Add(phase, new(boundary, null));
                }
                else
                {
                    if (!phaseWindows.TryGetValue(phase, out var window) || window.EndQpc is not null || boundary <= window.StartQpc)
                        throw new InvalidDataException($"Capture phase {phase} ended without one valid open window.");
                    phaseWindows[phase] = window with { EndQpc = boundary };
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(new EtwPipeEnvelope("phase-ack", Phase: phase,
                    PhaseStarted: transition == UsbEtwCapturePhaseTransition.Start, BoundaryQpc: boundary), JsonOptions));
            }

            var cancelled = command is null || command.Equals("cancel", StringComparison.Ordinal);
            foreach (var target in targets) await target.DetachAsync().ConfigureAwait(false);
            if (cancelled)
            {
                await CleanupAsync().ConfigureAwait(false);
                return 2;
            }

            var rawRecords = targets
                .SelectMany(target => target.Records.Select(record => new NamedRawRecord(target.ProcessName, record)))
                .OrderBy(item => item.Record.PerformanceCounterTimestamp)
                .ToList();
            var phaseOrdinals = new Dictionary<int, int>();
            var unattributedRecordCount = 0;
            var records = new List<ArmouryTapRecord>(rawRecords.Count);
            foreach (var item in rawRecords)
            {
                var phase = ClassifyPhase(item.Record.PerformanceCounterTimestamp, phaseWindows);
                if (phase < 0) { unattributedRecordCount++; continue; }
                var ordinal = phaseOrdinals.GetValueOrDefault(phase) + 1;
                phaseOrdinals[phase] = ordinal;
                records.Add(new(item.ProcessName, phase, ordinal, item.Record.Api,
                    item.Record.ApiResult, item.Record.LastError, item.Record.Report));
            }
            var reports = records.Select(record => new UsbEtwFeatureReport(
                DateTimeOffset.UnixEpoch,
                0,
                "AllyBindings-ArmouryTap",
                record.Api.ToString(),
                (int)record.Api,
                $"phase-{record.Phase}-ordinal-{record.Ordinal}",
                0,
                record.Report,
                Convert.ToHexString(SHA256.HashData(record.Report)).ToLowerInvariant())).ToList();
            var droppedCount = targets.Sum(target => target.DroppedRecordCount) + unattributedRecordCount;
            var output = new EtwCaptureOutput(
                [new("AllyBindings native user-mode HID write tap", Guid.Empty, 0)],
                rawRecords.Count, 0, 0, 0, 0,
                droppedCount,
                records.Sum(record => record.Report.Length),
                droppedCount != 0, false,
                reports, [], [], records);
            await CleanupAsync().ConfigureAwait(false);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new EtwPipeEnvelope("result", Output: output), JsonOptions));
            return 0;
        }
        finally
        {
            await CleanupAsync().ConfigureAwait(false);
        }
    }

    private static string DescribeSafeAttachRejection(Exception exception) => exception switch
    {
        TapAttachmentRejectedException rejection => rejection.Reason,
        UnauthorizedAccessException => "access-denied",
        System.ComponentModel.Win32Exception => "windows-api-rejected",
        TimeoutException => "tap-handshake-timeout",
        InvalidDataException => "tap-handshake-rejected",
        InvalidOperationException => "tap-attachment-rejected",
        _ => "tap-attachment-failed",
    };

    private static int ClassifyPhase(long qpc, IReadOnlyDictionary<int, PhaseWindow> windows)
    {
        foreach (var (phase, window) in windows.OrderBy(item => item.Value.StartQpc))
            if (window.EndQpc is long end && qpc >= window.StartQpc && qpc <= end) return phase;
        return windows.TryGetValue(1, out var first) && qpc < first.StartQpc ? 0 : -1;
    }

    private static List<VerifiedProcess> DiscoverSignedCandidates(
        SafeAccessTokenHandle unelevatedToken,
        out string diagnostic)
    {
        var found = new List<VerifiedProcess>();
        var observations = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Observe(string name, string result)
        {
            if (!observations.TryGetValue(name, out var results))
            {
                results = new(StringComparer.Ordinal);
                observations.Add(name, results);
            }
            results.Add(result);
        }

        foreach (var name in ArmouryTapProtocol.ExactCandidateProcessNames)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch
            {
                Observe(name, "enumeration-failed");
                continue;
            }
            foreach (var process in processes)
            {
                Observe(name, "running");
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (path is null) { Observe(name, "path-unavailable"); continue; }
                        if (!IsTrustedInstallPath(path)) { Observe(name, "untrusted-install-root"); continue; }
                        if (HasReparseTraversal(path)) { Observe(name, "reparse-traversal"); continue; }
                        if (IsWritableByToken(path, unelevatedToken)) { Observe(name, "user-writable-image-or-parent"); continue; }
                        if (!IsNativeAmd64(process.Handle)) { Observe(name, "not-native-x64"); continue; }
                        FileStream? imageLock = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        try
                        {
                            if (!HasAsusAuthenticodeSignature(path))
                            {
                                Observe(name, "asus-signature-rejected");
                                continue;
                            }
                            var imageHash = SHA256.HashData(imageLock);
                            imageLock.Position = 0;
                            if (found.Any(item => item.ProcessId == process.Id)) continue;
                            var processStartTimeUtc = process.StartTime.ToUniversalTime();
                            var lifecycleHandle = OpenProcess(0x00100000 | 0x1000, false, process.Id);
                            if (lifecycleHandle.IsInvalid)
                            {
                                lifecycleHandle.Dispose();
                                Observe(name, "lifecycle-handle-denied");
                                continue;
                            }
                            found.Add(new(process.Id, processStartTimeUtc, Path.GetFullPath(path), name,
                                imageLock, imageHash, unelevatedToken, lifecycleHandle));
                            Observe(name, "accepted");
                            imageLock = null;
                        }
                        finally { imageLock?.Dispose(); }
                    }
                    catch (UnauthorizedAccessException) { Observe(name, "inspection-denied"); }
                    catch { Observe(name, "inspection-failed"); }
                }
            }
        }
        diagnostic = observations.Count == 0
            ? $"None of the {ArmouryTapProtocol.ExactCandidateProcessNames.Count} exact Armoury component names were running. Open Armoury Crate, leave it open on the controller configuration page, then retry."
            : "Allowlisted process observations: " + string.Join("; ", observations
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{item.Key}=[{string.Join(',', item.Value.Order(StringComparer.Ordinal))}]")) + ".";
        return found;
    }

    internal static bool Revalidate(VerifiedProcess expected)
    {
        if (!ArmouryTapProtocol.IsExactCandidateProcessName(expected.ExactName)) return false;
        try
        {
            using var process = Process.GetProcessById(expected.ProcessId);
            var currentName = process.ProcessName;
            var currentPath = process.MainModule?.FileName;
            return ArmouryTapProtocol.IsExactCandidateProcessName(currentName) &&
                currentName.Equals(expected.ExactName, StringComparison.OrdinalIgnoreCase) &&
                process.StartTime.ToUniversalTime() == expected.StartTimeUtc && currentPath is not null &&
                Path.GetFullPath(currentPath).Equals(expected.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                IsTrustedInstallPath(currentPath) && !HasReparseTraversal(currentPath) &&
                !IsWritableByToken(currentPath, expected.UnelevatedToken) && IsNativeAmd64(process.Handle) &&
                HasAsusAuthenticodeSignature(currentPath) && ImageHashMatches(expected);
        }
        catch { return false; }
    }

    private static bool HasAsusAuthenticodeSignature(string path)
    {
        var file = new WinTrustFileInfo(path);
        var data = new WinTrustData(file);
        try
        {
            if (WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, data.Pointer) != 0) return false;
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            var subjectParts = certificate.Subject.Split(',', StringSplitOptions.TrimEntries |
                StringSplitOptions.RemoveEmptyEntries);
            var exactCommonName = subjectParts.Any(part =>
                part.Equals("CN=ASUSTeK COMPUTER INC.", StringComparison.OrdinalIgnoreCase));
            var exactOrganization = subjectParts.Any(part =>
                part.Equals("O=ASUSTeK COMPUTER INC.", StringComparison.OrdinalIgnoreCase));
            var codeSigningEku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>()
                    .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.3"));
            var keyUsage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            return exactCommonName && exactOrganization && codeSigningEku &&
                (keyUsage is null || (keyUsage.KeyUsages & X509KeyUsageFlags.DigitalSignature) != 0);
        }
        catch { return false; }
        finally { data.Dispose(); file.Dispose(); }
    }

    private static bool IsNativeAmd64(IntPtr process) =>
        IsWow64Process2(process, out var processMachine, out var nativeMachine) &&
        processMachine == 0 && nativeMachine == 0x8664;

    private static string? GetTrustedInstallRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };
        return roots.Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault(root => fullPath.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTrustedInstallPath(string path) => GetTrustedInstallRoot(path) is not null;

    private static bool HasReparseTraversal(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            if (string.Equals(Path.GetPathRoot(current), current, StringComparison.OrdinalIgnoreCase)) break;
        }
        return false;
    }

    private static SafeAccessTokenHandle OpenParentImpersonationToken(int processId)
    {
        using var process = Process.GetProcessById(processId);
        if (!OpenProcessToken(process.Handle, 0x0008 | 0x0002, out var primaryToken))
            throw new System.ComponentModel.Win32Exception();
        using (primaryToken)
        {
            if (!DuplicateToken(primaryToken, 2, out var impersonationToken))
                throw new System.ComponentModel.Win32Exception();
            return impersonationToken;
        }
    }

    private static bool IsWritableByToken(string path, SafeAccessTokenHandle token)
    {
        const uint dangerousRights = 0x00000002 | 0x00000004 | 0x00000040 |
            0x00010000 | 0x00040000 | 0x00080000;
        var trustedRoot = GetTrustedInstallRoot(path)
            ?? throw new InvalidOperationException("Executable is outside every trusted install root.");
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            const AccessControlSections sections = AccessControlSections.Access |
                AccessControlSections.Owner | AccessControlSections.Group;
            FileSystemSecurity security = File.Exists(current)
                ? new FileInfo(current).GetAccessControl(sections)
                : new DirectoryInfo(current).GetAccessControl(sections);
            if ((GetMaximumAllowedAccess(security, token) & dangerousRights) != 0) return true;
            if (current.Equals(trustedRoot, StringComparison.OrdinalIgnoreCase)) break;
        }
        return false;
    }

    private static uint GetMaximumAllowedAccess(FileSystemSecurity security, SafeAccessTokenHandle token)
    {
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pinnedDescriptor = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var mapping = new GenericMapping
            {
                GenericRead = 0x00120089,
                GenericWrite = 0x00120116,
                GenericExecute = 0x001200A0,
                GenericAll = 0x001F01FF,
            };
            uint privilegeSetLength = 256;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var privilegeSet = Marshal.AllocHGlobal(checked((int)privilegeSetLength));
                try
                {
                    if (AccessCheck(pinnedDescriptor.AddrOfPinnedObject(), token, 0x02000000, ref mapping,
                        privilegeSet, ref privilegeSetLength, out var grantedAccess, out var accessStatus))
                        return accessStatus ? grantedAccess : 0;
                    var error = Marshal.GetLastWin32Error();
                    if (error != 122) throw new System.ComponentModel.Win32Exception(error);
                }
                finally { Marshal.FreeHGlobal(privilegeSet); }
            }
            throw new IOException("Windows returned an unstable AccessCheck privilege-set size.");
        }
        finally { pinnedDescriptor.Free(); }
    }

    private static bool ImageHashMatches(VerifiedProcess process)
    {
        process.ImageLock.Position = 0;
        var actual = SHA256.HashData(process.ImageLock);
        process.ImageLock.Position = 0;
        return CryptographicOperations.FixedTimeEquals(actual, process.ImageHash);
    }

    private static string CreatePrivateExtractionDirectory(Guid sessionId)
    {
        var windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var tempRoot = Path.Combine(windowsRoot, "Temp");
        if (!Directory.Exists(tempRoot) || HasReparseTraversal(tempRoot))
            throw new InvalidOperationException("The trusted Windows temporary root is unavailable or traverses a reparse point.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Current Windows SID unavailable.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.SetOwner(administrators);
        security.AddAccessRule(new(user, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        foreach (var sid in new[] { system, administrators })
            security.AddAccessRule(new(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pinnedDescriptor = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = pinnedDescriptor.AddrOfPinnedObject(),
            };
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var randomSuffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                var root = Path.Combine(tempRoot, $"AllyBindings-armoury-tap-{sessionId:D}-{randomSuffix}");
                if (CreateDirectoryW(root, ref attributes)) return root;
                var error = Marshal.GetLastWin32Error();
                if (error != 183) throw new System.ComponentModel.Win32Exception(error);
            }
        }
        finally { pinnedDescriptor.Free(); }
        throw new IOException("Windows could not allocate a unique private Armoury tap directory.");
    }

    private static byte[] ExtractNativeDll(string destination)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(NativeResourceName)
            ?? throw new TapUnavailableException("The embedded native Armoury tap is unavailable; ETW fallback is required.");
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            hasher.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        output.Flush(true);
        return hasher.GetHashAndReset();
    }

    private static FileStream LockAndVerifyNativeDll(string path, byte[] expectedHash)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actualHash = SHA256.HashData(stream);
        stream.Position = 0;
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            stream.Dispose();
            throw new InvalidDataException("The extracted native tap DLL failed its embedded-resource SHA-256 check.");
        }
        return stream;
    }

    private static void DeleteExtractionDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        Exception? failure = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Directory.Delete(path, true); return; }
            catch (Exception ex) { failure = ex; Thread.Sleep(100); }
        }
        throw new IOException("The temporary native tap files could not be deleted after hook teardown.", failure);
    }

    private static void VerifyParentExecutableIdentity(int parentProcessId)
    {
        var helperPath = Environment.ProcessPath ?? throw new InvalidOperationException("Helper executable path unavailable.");
        using var parent = Process.GetProcessById(parentProcessId);
        var parentPath = parent.MainModule?.FileName ?? throw new InvalidOperationException("Parent executable path unavailable.");
        if (!Path.GetFullPath(parentPath).Equals(Path.GetFullPath(helperPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The tap pipe server is not the same Ally Bindings executable as the helper.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);
    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal sealed record VerifiedProcess(int ProcessId, DateTime StartTimeUtc, string ExecutablePath,
        string ExactName, FileStream ImageLock, byte[] ImageHash, SafeAccessTokenHandle UnelevatedToken,
        SafeProcessHandle LifecycleHandle);
    private sealed record PhaseWindow(long StartQpc, long? EndQpc);
    private sealed record RawTapRecord(int ProcessId, ArmouryTapApi Api, long PerformanceCounterTimestamp,
        bool ApiResult, int LastError, byte[] Report);
    private sealed record NamedRawRecord(string ProcessName, RawTapRecord Record);

    private sealed class WinTrustFileInfo : IDisposable
    {
        private readonly IntPtr _pathPointer;
        public IntPtr Pointer { get; }
        public WinTrustFileInfo(string path)
        {
            _pathPointer = Marshal.StringToCoTaskMemUni(path);
            var native = new WinTrustFileInfoNative
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfoNative>(),
                FilePath = _pathPointer,
                FileHandle = IntPtr.Zero,
                KnownSubject = IntPtr.Zero,
            };
            Pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfoNative>());
            Marshal.StructureToPtr(native, Pointer, false);
        }
        public void Dispose()
        {
            if (Pointer == IntPtr.Zero) return;
            Marshal.FreeCoTaskMem(_pathPointer);
            Marshal.FreeCoTaskMem(Pointer);
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfoNative
        { public uint Size; public IntPtr FilePath; public IntPtr FileHandle; public IntPtr KnownSubject; }
    }

    private sealed class WinTrustData : IDisposable
    {
        public IntPtr Pointer { get; }
        public WinTrustData(WinTrustFileInfo file)
        {
            var native = new WinTrustDataNative
            {
                Size = (uint)Marshal.SizeOf<WinTrustDataNative>(),
                UIChoice = 2,
                RevocationChecks = 1,
                UnionChoice = 1,
                FileInfo = file.Pointer,
                StateAction = 0,
                ProvFlags = 0x00001040,
                UIContext = 0,
            };
            Pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustDataNative>());
            Marshal.StructureToPtr(native, Pointer, false);
        }
        public void Dispose() => Marshal.FreeCoTaskMem(Pointer);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustDataNative
        {
            public uint Size; public IntPtr PolicyCallbackData; public IntPtr SipClientData; public uint UIChoice;
            public uint RevocationChecks; public uint UnionChoice; public IntPtr FileInfo; public uint StateAction;
            public IntPtr StateData; public IntPtr UrlReference; public uint ProvFlags; public uint UIContext;
        }
    }

    private sealed class TappedProcess : IDisposable
    {
        private readonly VerifiedProcess _identity;
        private readonly string _dllPath;
        private readonly byte[] _token;
        private readonly NamedPipeServerStream _pipe;
        private readonly CancellationTokenSource _readerCancellation = new();
        private Task _readerTask;
        private IntPtr _remoteModule;
        private int _detachState;
        public string ProcessName => _identity.ExactName;
        public List<RawTapRecord> Records { get; } = [];
        public int DroppedRecordCount { get; private set; }

        private TappedProcess(VerifiedProcess identity, string dllPath, byte[] token, NamedPipeServerStream pipe,
            IntPtr remoteModule, Task readerTask)
        { _identity = identity; _dllPath = dllPath; _token = token; _pipe = pipe; _remoteModule = remoteModule; _readerTask = readerTask; }

        public static async Task<TappedProcess> AttachAsync(Guid sessionId, VerifiedProcess identity, string dllPath,
            CancellationToken cancellationToken)
        {
            if (!Revalidate(identity)) throw new InvalidOperationException($"ASUS process {identity.ProcessId} changed before injection.");
            var token = RandomNumberGenerator.GetBytes(32);
            var pipeName = $"AllyBindings.ArmouryTap.{sessionId:D}.{identity.ProcessId}";
            var configPath = dllPath + ".config";
            var pipe = CreateTapServer(pipeName);
            IntPtr remoteModule = IntPtr.Zero;
            try
            {
                var configBytes = Encoding.ASCII.GetBytes(
                    $"pipe=\\\\.\\pipe\\{pipeName}\ntoken={Convert.ToHexString(token)}\nhelper={Environment.ProcessId}\n");
                await using (var configWriter = new FileStream(
                    configPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await configWriter.WriteAsync(configBytes, cancellationToken).ConfigureAwait(false);
                    configWriter.Flush(true);
                }
                // Hold a read-only anti-tamper lock. Keeping the original write
                // handle open makes the injected DLL's FILE_SHARE_READ open fail
                // Windows' symmetric sharing check before it can handshake.
                using var configLock = new FileStream(
                    configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                remoteModule = Inject(identity, dllPath);
                var holder = new TappedProcess(identity, dllPath, token, pipe, remoteModule, Task.CompletedTask);
                var connectTask = pipe.WaitForConnectionAsync(cancellationToken);
                try
                {
                    await connectTask.WaitAsync(ArmouryTapProtocol.CandidateHandshakeStepTimeout, cancellationToken);
                }
                catch (TimeoutException ex)
                {
                    throw new TapAttachmentRejectedException("tap-pipe-connect-timeout", ex);
                }
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid != (uint)identity.ProcessId)
                    throw new InvalidOperationException("A tap record pipe connected from an unexpected process.");
                Wire? ready;
                try
                {
                    ready = await ReadRecordAsync(pipe, cancellationToken)
                        .WaitAsync(ArmouryTapProtocol.CandidateHandshakeStepTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    throw new TapAttachmentRejectedException("tap-ready-timeout", ex);
                }
                if (ready is null)
                    throw new TapAttachmentRejectedException("tap-worker-exited-before-ready");
                if (ready.ProcessId != identity.ProcessId || ready.Api != 0 ||
                    !CryptographicOperations.FixedTimeEquals(ready.Token, token))
                    throw new InvalidDataException("The injected tap did not authenticate its ready record.");
                if (ready.ApiResult != 0)
                    throw new TapAttachmentRejectedException(
                        $"tap-hook-stage-{ready.ApiResult}-detail-{unchecked((uint)ready.LastError)}");
                holder._readerTask = holder.ReadLoopAsync();
                configLock.Dispose();
                File.Delete(configPath);
                return holder;
            }
            catch (Exception attachFailure)
            {
                Exception? teardownFailure = null;
                if (remoteModule != IntPtr.Zero)
                {
                    if (!Revalidate(identity))
                        teardownFailure = new InvalidOperationException("The injected process identity changed before failed-attach cleanup.");
                    else
                    {
                        try { StopAndUnload(identity, dllPath, remoteModule); }
                        catch (Exception ex) { teardownFailure = ex; }
                    }
                }
                pipe.Dispose();
                try { File.Delete(configPath); } catch (Exception ex) { teardownFailure ??= ex; }
                if (teardownFailure is not null)
                    throw new TapTeardownUnconfirmedException("Failed tap attachment could not be safely rolled back.",
                        new AggregateException(attachFailure, teardownFailure));
                throw;
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_readerCancellation.IsCancellationRequested)
                {
                    var wire = await ReadRecordAsync(_pipe, _readerCancellation.Token);
                    if (wire is null) return;
                    if (wire.ProcessId != _identity.ProcessId || !CryptographicOperations.FixedTimeEquals(wire.Token, _token))
                        throw new InvalidDataException("An unauthenticated tap record was rejected.");
                    if (wire.Api == 0xFF)
                    {
                        DroppedRecordCount += checked((int)wire.ApiResult);
                        continue;
                    }
                    if (wire.Api is not 1 and not 2 || !ArmouryTapProtocol.IsRetainableReport(wire.Report)) continue;
                    lock (Records)
                    {
                        if (Records.Count == ArmouryTapProtocol.MaximumRecords) { DroppedRecordCount++; continue; }
                        Records.Add(new(wire.ProcessId, (ArmouryTapApi)wire.Api, wire.Qpc,
                            wire.ApiResult != 0, wire.LastError, wire.Report));
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public async Task DetachAsync()
        {
            var priorState = Interlocked.CompareExchange(ref _detachState, 1, 0);
            if (priorState == 2) return;
            if (priorState != 0) throw new InvalidOperationException("Native hook teardown is already in progress.");
            try
            {
                var processWait = WaitForSingleObject(_identity.LifecycleHandle, 0);
                if (processWait == 0)
                {
                    _remoteModule = IntPtr.Zero;
                    _readerCancellation.Cancel();
                    Volatile.Write(ref _detachState, 2);
                    return;
                }
                if (processWait != 258) throw new System.ComponentModel.Win32Exception();
                if (_remoteModule != IntPtr.Zero)
                {
                    if (!Revalidate(_identity)) throw new InvalidOperationException("The tapped ASUS process identity changed before hook teardown.");
                    StopAndUnload(_identity, _dllPath, _remoteModule);
                    _remoteModule = IntPtr.Zero;
                }
                await _readerTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                _readerCancellation.Cancel();
                Volatile.Write(ref _detachState, 2);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _detachState, 0);
                throw new InvalidOperationException("Native hook unload could not be confirmed; teardown remains retryable.", ex);
            }
        }

        public void Dispose() { _readerCancellation.Cancel(); _readerCancellation.Dispose(); _pipe.Dispose(); }

        private sealed record Wire(int ProcessId, byte Api, long Qpc, uint ApiResult, int LastError, byte[] Token, byte[] Report);
        private static async Task<Wire?> ReadRecordAsync(Stream stream, CancellationToken cancellationToken)
        {
            var bytes = new byte[ArmouryTapProtocol.WireRecordSize];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
                if (read == 0) return offset == 0 ? null : throw new EndOfStreamException("Truncated tap record.");
                offset += read;
            }
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != ArmouryTapProtocol.WireMagic ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) != ArmouryTapProtocol.WireVersion)
                throw new InvalidDataException("Invalid tap record framing.");
            var length = bytes[7];
            if (length > ArmouryTapProtocol.MaximumReportLength) throw new InvalidDataException("Oversized tap record.");
            return new(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)), bytes[6],
                BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(12)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)), bytes[28..60], bytes[60..(60 + length)]);
        }

        private static NamedPipeServerStream CreateTapServer(string name)
        {
            var security = CreateTapPipeSecurity();
            var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, 0, security, HandleInheritability.None);
            try
            {
                ApplyMediumIntegrityLabel(server.SafePipeHandle);
                var label = ReadTapPipeIntegrityLabel(server.SafePipeHandle);
                if (!label.Contains("(ML;;NW;;;ME)", StringComparison.Ordinal) &&
                    !label.Contains("(ML;;NW;;;S-1-16-8192)", StringComparison.Ordinal))
                    throw new System.Security.SecurityException(
                        $"Tap pipe mandatory label verification failed: {label}");
                return server;
            }
            catch
            {
                server.Dispose();
                throw;
            }
        }

        private static PipeSecurity CreateTapPipeSecurity()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(identity.User!);
            security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return security;
        }

        private static void ApplyMediumIntegrityLabel(SafePipeHandle pipe)
        {
            const uint labelSecurityInformation = 0x00000010;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                    "S:(ML;;NW;;;ME)", 1, out var descriptor, out _))
                throw new System.ComponentModel.Win32Exception();
            try
            {
                if (!GetSecurityDescriptorSacl(descriptor, out var present, out var sacl, out _) ||
                    !present || sacl == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception();
                var result = SetSecurityInfo(pipe.DangerousGetHandle(), 6, labelSecurityInformation,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
                if (result != 0) throw new System.ComponentModel.Win32Exception(checked((int)result));
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        private static string ReadTapPipeIntegrityLabel(SafePipeHandle pipe)
        {
            const uint labelSecurityInformation = 0x00000010;
            var result = GetSecurityInfo(pipe.DangerousGetHandle(), 6, labelSecurityInformation,
                out _, out _, out _, out _, out var descriptor);
            if (result != 0) throw new System.ComponentModel.Win32Exception(checked((int)result));
            try
            {
                if (!ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, 1,
                        labelSecurityInformation, out var text, out _))
                    throw new System.ComponentModel.Win32Exception();
                try
                {
                    return Marshal.PtrToStringUni(text)
                        ?? throw new InvalidDataException("Windows returned an empty tap pipe mandatory label.");
                }
                finally
                {
                    LocalFree(text);
                }
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
            string text, uint revision, out IntPtr descriptor, out uint descriptorSize);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetSecurityDescriptorSacl(
            IntPtr descriptor, out bool present, out IntPtr sacl, out bool defaulted);
        [DllImport("advapi32.dll")]
        private static extern uint SetSecurityInfo(IntPtr handle, int objectType, uint securityInformation,
            IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
        [DllImport("advapi32.dll")]
        private static extern uint GetSecurityInfo(IntPtr handle, int objectType, uint securityInformation,
            out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "ConvertSecurityDescriptorToStringSecurityDescriptorW")]
        private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(
            IntPtr descriptor, uint revision, uint securityInformation, out IntPtr text, out uint textLength);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }

    private static IntPtr Inject(VerifiedProcess identity, string dllPath)
    {
        if (!Revalidate(identity)) throw new InvalidOperationException("Candidate identity failed immediate pre-injection validation.");
        using var process = OpenTarget(identity.ProcessId);
        var bytes = Encoding.Unicode.GetBytes(dllPath + '\0');
        var remotePath = VirtualAllocEx(process, IntPtr.Zero, (nuint)bytes.Length, 0x3000, 0x04);
        if (remotePath == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var releaseRemotePath = true;
        try
        {
            if (!WriteProcessMemory(process, remotePath, bytes, bytes.Length, out var written) || written != bytes.Length)
                throw new System.ComponentModel.Win32Exception();
            var loadLibrary = ResolveRemoteProc(identity.ProcessId, "kernel32.dll", "LoadLibraryW");
            using var thread = CreateRemoteThread(process, IntPtr.Zero, 0, loadLibrary, remotePath, 0, out _);
            if (thread.IsInvalid) throw new System.ComponentModel.Win32Exception();
            var wait = WaitForSingleObject(thread, ArmouryTapProtocol.CandidateRemoteCallTimeoutMilliseconds);
            if (wait != 0)
            {
                releaseRemotePath = false;
                throw new TapTeardownUnconfirmedException(
                    "The remote LoadLibrary call did not terminate; its argument memory was intentionally retained.");
            }
            var hasExitCode = GetExitCodeThread(thread, out var exitCode);
            var remoteModule = FindRemoteModuleByPathWithRetry(identity.ProcessId, dllPath);
            if (remoteModule != IntPtr.Zero) return remoteModule;
            if (hasExitCode && exitCode == 0)
                throw new InvalidOperationException("Remote LoadLibraryW rejected the native tap DLL.");
            throw new TapTeardownUnconfirmedException(
                "LoadLibraryW may have succeeded, but the loaded module could not be positively located for cleanup.");
        }
        finally
        {
            if (releaseRemotePath) VirtualFreeEx(process, remotePath, 0, 0x8000);
        }
    }

    private static void StopAndUnload(VerifiedProcess identity, string dllPath, IntPtr remoteModule)
    {
        using var process = OpenTarget(identity.ProcessId);
        var stopRva = ReadExportRva(dllPath, "ArmouryTapStop");
        RunRemote(process, remoteModule + checked((int)stopRva), IntPtr.Zero);
        var freeLibrary = ResolveRemoteProc(identity.ProcessId, "kernel32.dll", "FreeLibrary");
        RunRemote(process, freeLibrary, remoteModule);
        if (FindRemoteModuleByPath(identity.ProcessId, dllPath) != IntPtr.Zero)
            throw new InvalidOperationException("The native tap DLL remained loaded after FreeLibrary.");
    }

    private static IntPtr FindRemoteModuleByPathWithRetry(int pid, string modulePath)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var module = FindRemoteModuleByPath(pid, modulePath);
            if (module != IntPtr.Zero) return module;
            Thread.Sleep(50);
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindRemoteModuleByPath(int pid, string modulePath)
    {
        var expected = Path.GetFullPath(modulePath);
        var snapshot = CreateToolhelp32Snapshot(0x00000008 | 0x00000010, pid);
        if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception();
        try
        {
            var entry = new ModuleEntry32 { Size = (uint)Marshal.SizeOf<ModuleEntry32>() };
            if (!Module32First(snapshot, ref entry))
            {
                var firstError = Marshal.GetLastWin32Error();
                if (firstError == 18) return IntPtr.Zero;
                throw new System.ComponentModel.Win32Exception(firstError);
            }
            while (true)
            {
                if (Path.GetFullPath(entry.ExePath).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return entry.BaseAddress;
                entry.Size = (uint)Marshal.SizeOf<ModuleEntry32>();
                if (Module32Next(snapshot, ref entry)) continue;
                var nextError = Marshal.GetLastWin32Error();
                if (nextError == 18) return IntPtr.Zero;
                throw new System.ComponentModel.Win32Exception(nextError);
            }
        }
        finally { CloseHandle(snapshot); }
    }

    private static SafeProcessHandle OpenTarget(int pid)
    {
        var handle = OpenProcess(0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400 | 0x00100000, false, pid);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception();
        return handle;
    }

    private static void RunRemote(SafeProcessHandle process, IntPtr start, IntPtr parameter)
    {
        using var thread = CreateRemoteThread(process, IntPtr.Zero, 0, start, parameter, 0, out _);
        if (thread.IsInvalid) throw new System.ComponentModel.Win32Exception();
        if (WaitForSingleObject(thread, ArmouryTapProtocol.CandidateRemoteCallTimeoutMilliseconds) != 0)
            throw new TimeoutException("Remote tap lifecycle call timed out.");
        if (!GetExitCodeThread(thread, out var exitCode) || exitCode == 0)
            throw new InvalidOperationException("Remote tap lifecycle call failed.");
    }

    private static IntPtr ResolveRemoteProc(int pid, string moduleName, string export)
    {
        var localModule = GetModuleHandle(moduleName);
        if (localModule == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var localProc = GetProcAddress(localModule, export);
        if (localProc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        const uint fromAddressUnchangedReference = 0x00000004 | 0x00000002;
        if (!GetModuleHandleExW(fromAddressUnchangedReference, localProc, out var owningModule) ||
            owningModule == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();
        var owningPath = new StringBuilder(32768);
        if (GetModuleFileNameW(owningModule, owningPath, owningPath.Capacity) == 0)
            throw new System.ComponentModel.Win32Exception();
        var ownerName = Path.GetFileName(owningPath.ToString());
        if (string.IsNullOrWhiteSpace(ownerName)) throw new InvalidOperationException("Resolved export owner has no module name.");
        var offset = localProc.ToInt64() - owningModule.ToInt64();
        if (offset < 0 || offset > int.MaxValue) throw new InvalidOperationException("Resolved export lies outside its owning module.");
        return FindRemoteModule(pid, ownerName) + checked((int)offset);
    }

    private static IntPtr FindRemoteModule(int pid, string moduleName, bool throwIfMissing = true)
    {
        var snapshot = CreateToolhelp32Snapshot(0x00000008 | 0x00000010, pid);
        if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception();
        try
        {
            var entry = new ModuleEntry32 { Size = (uint)Marshal.SizeOf<ModuleEntry32>() };
            if (Module32First(snapshot, ref entry)) do
                {
                    if (entry.Module.Equals(moduleName, StringComparison.OrdinalIgnoreCase)) return entry.BaseAddress;
                } while (Module32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        if (throwIfMissing) throw new InvalidOperationException($"Remote module {moduleName} was not found.");
        return IntPtr.Zero;
    }

    internal static uint ReadExportRva(string path, string name)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        stream.Position = 0x3C;
        var pe = reader.ReadInt32();
        stream.Position = pe + 6;
        var sections = reader.ReadUInt16();
        stream.Position = pe + 20;
        var optionalSize = reader.ReadUInt16();
        stream.Position = pe + 24;
        if (reader.ReadUInt16() != 0x20B) throw new InvalidDataException("Tap DLL is not PE32+.");
        stream.Position = pe + 24 + 112;
        var exportRva = reader.ReadUInt32();
        stream.Position = pe + 24 + optionalSize;
        var headers = new List<(uint Va, uint Size, uint Raw)>();
        for (var i = 0; i < sections; i++)
        {
            stream.Position += 8;
            var virtualSize = reader.ReadUInt32(); var va = reader.ReadUInt32();
            var rawSize = reader.ReadUInt32(); var raw = reader.ReadUInt32();
            stream.Position += 16;
            headers.Add((va, Math.Max(virtualSize, rawSize), raw));
        }
        long Offset(uint rva) { var s = headers.Single(item => rva >= item.Va && rva < item.Va + item.Size); return s.Raw + rva - s.Va; }
        stream.Position = Offset(exportRva) + 24;
        var numberOfNames = reader.ReadUInt32();
        var functionsRva = reader.ReadUInt32(); var namesRva = reader.ReadUInt32(); var ordinalsRva = reader.ReadUInt32();
        for (uint i = 0; i < numberOfNames; i++)
        {
            stream.Position = Offset(namesRva + i * 4); var stringRva = reader.ReadUInt32();
            stream.Position = Offset(stringRva); var bytes = new List<byte>(); byte value; while ((value = reader.ReadByte()) != 0) bytes.Add(value);
            if (!Encoding.ASCII.GetString(bytes.ToArray()).Equals(name, StringComparison.Ordinal)) continue;
            stream.Position = Offset(ordinalsRva + i * 2); var ordinal = reader.ReadUInt16();
            stream.Position = Offset(functionsRva + (uint)ordinal * 4u); return reader.ReadUInt32();
        }
        throw new InvalidDataException($"Export {name} is missing from the native tap.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ModuleEntry32
    {
        public uint Size; public uint ModuleId; public uint ProcessId; public uint GlobalUsage; public uint ProcessUsage;
        public IntPtr BaseAddress; public uint BaseSize; public IntPtr ModuleHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Module;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExePath;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping
    {
        public uint GenericRead;
        public uint GenericWrite;
        public uint GenericExecute;
        public uint GenericAll;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, ref SecurityAttributes securityAttributes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateToken(
        SafeAccessTokenHandle existingToken, int impersonationLevel, out SafeAccessTokenHandle duplicateToken);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AccessCheck(IntPtr securityDescriptor, SafeAccessTokenHandle clientToken,
        uint desiredAccess, ref GenericMapping genericMapping, IntPtr privilegeSet,
        ref uint privilegeSetLength, out uint grantedAccess, [MarshalAs(UnmanagedType.Bool)] out bool accessStatus);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(SafeProcessHandle process, IntPtr address, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(SafeProcessHandle process, IntPtr address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(SafeProcessHandle process, IntPtr address, byte[] buffer, int size, out int written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle CreateRemoteThread(SafeProcessHandle process, IntPtr attributes, nuint stack, IntPtr start, IntPtr parameter, uint flags, out uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeThread(SafeWaitHandle thread, out uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder path, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Module32First(IntPtr snapshot, ref ModuleEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Module32Next(IntPtr snapshot, ref ModuleEntry32 entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    private sealed class TapUnavailableException(string message) : InvalidOperationException(message);
    private sealed class TapAttachmentRejectedException(string reason, Exception? innerException = null)
        : InvalidOperationException(reason, innerException)
    {
        public string Reason { get; } = reason;
    }
    private sealed class TapTeardownUnconfirmedException(string message, Exception? innerException = null)
        : InvalidOperationException(message, innerException);
}
