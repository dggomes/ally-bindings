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
    private static readonly TimeSpan MaximumCaptureDuration = TimeSpan.FromMinutes(10);
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
            return await CaptureAsync(sessionId, reader, writer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (pipe.IsConnected)
            {
                try
                {
                    await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new EtwPipeEnvelope("error", Error: ex.Message), JsonOptions));
                }
                catch { }
            }
            return 1;
        }
    }

    private static async Task<int> CaptureAsync(Guid sessionId, BoundedTextLineReader reader, StreamWriter writer,
        CancellationToken cancellationToken)
    {
        var extractionDirectory = CreatePrivateExtractionDirectory(sessionId);
        var dllPath = Path.Combine(extractionDirectory, "AllyBindings.ArmouryTap.dll");
        var targets = new List<TappedProcess>();
        try
        {
            ExtractNativeDll(dllPath);
            var candidates = DiscoverSignedCandidates();
            if (candidates.Count == 0)
                throw new InvalidOperationException("No running ASUS-signed Armoury candidate process was found for the user-mode tap.");

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                targets.Add(await TappedProcess.AttachAsync(sessionId, candidate, dllPath, cancellationToken).ConfigureAwait(false));
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
                await writer.WriteLineAsync(JsonSerializer.Serialize(new EtwPipeEnvelope("phase-ack", Phase: phase,
                    PhaseStarted: transition == UsbEtwCapturePhaseTransition.Start, BoundaryQpc: boundary), JsonOptions));
            }

            var cancelled = command is null || command.Equals("cancel", StringComparison.Ordinal);
            foreach (var target in targets) await target.DetachAsync().ConfigureAwait(false);
            if (cancelled) return 2;

            var records = targets.SelectMany(target => target.Records).OrderBy(record => record.PerformanceCounterTimestamp).ToList();
            var reports = records.Select(record => new UsbEtwFeatureReport(
                DateTimeOffset.UnixEpoch.AddSeconds((double)record.PerformanceCounterTimestamp / Stopwatch.Frequency),
                record.PerformanceCounterTimestamp,
                "AllyBindings-ArmouryTap",
                record.Api.ToString(),
                (int)record.Api,
                $"pid-{record.ProcessId}",
                0,
                record.Report,
                Convert.ToHexString(SHA256.HashData(record.Report)).ToLowerInvariant())).ToList();
            var output = new EtwCaptureOutput(
                [new("AllyBindings native user-mode HID write tap", Guid.Empty, 0)],
                records.Count, 0, 0, 0, 0,
                targets.Sum(target => target.DroppedRecordCount),
                records.Sum(record => record.Report.Length),
                targets.Any(target => target.DroppedRecordCount != 0), false,
                reports, [], [], records);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new EtwPipeEnvelope("result", Output: output), JsonOptions));
            return 0;
        }
        finally
        {
            foreach (var target in targets)
            {
                try { await target.DetachAsync().ConfigureAwait(false); } catch { }
                target.Dispose();
            }
            DeleteExtractionDirectory(extractionDirectory);
        }
    }

    private static List<VerifiedProcess> DiscoverSignedCandidates()
    {
        var found = new List<VerifiedProcess>();
        foreach (var name in ArmouryTapProtocol.ExactCandidateProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (path is null || !IsNativeAmd64(process.Handle) || !HasAsusAuthenticodeSignature(path)) continue;
                        found.Add(new(process.Id, process.StartTime.ToUniversalTime(), Path.GetFullPath(path), name));
                    }
                    catch { }
                }
            }
        }
        return found.GroupBy(item => item.ProcessId).Select(group => group.Single()).ToList();
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
                IsNativeAmd64(process.Handle) && HasAsusAuthenticodeSignature(currentPath);
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
            return certificate.Subject.Contains("ASUSTeK COMPUTER INC.", StringComparison.OrdinalIgnoreCase) ||
                certificate.Subject.Contains("ASUS", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
        finally { data.Dispose(); file.Dispose(); }
    }

    private static bool IsNativeAmd64(IntPtr process) =>
        IsWow64Process2(process, out var processMachine, out var nativeMachine) &&
        processMachine == 0 && nativeMachine == 0x8664;

    private static string CreatePrivateExtractionDirectory(Guid sessionId)
    {
        var root = Path.Combine(Path.GetTempPath(), "AllyBindings", "armoury-tap", sessionId.ToString("D"));
        Directory.CreateDirectory(root);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Current Windows SID unavailable.");
        security.SetOwner(user);
        foreach (var sid in new SecurityIdentifier[] { user,
            new(WellKnownSidType.LocalSystemSid, null), new(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(security);
        return root;
    }

    private static void ExtractNativeDll(string destination)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(NativeResourceName)
            ?? throw new FileNotFoundException("The embedded native Armoury tap is unavailable; ETW fallback is required.");
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        source.CopyTo(output);
        output.Flush(true);
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

    internal sealed record VerifiedProcess(int ProcessId, DateTime StartTimeUtc, string ExecutablePath, string ExactName);

    private sealed class WinTrustFileInfo : IDisposable
    {
        public IntPtr Pointer { get; }
        public WinTrustFileInfo(string path)
        {
            var pathPointer = Marshal.StringToCoTaskMemUni(path);
            Pointer = Marshal.AllocCoTaskMem(IntPtr.Size * 3 + 8);
            Marshal.WriteInt32(Pointer, Marshal.SizeOf<WinTrustFileInfoNative>());
            Marshal.WriteIntPtr(Pointer, 8, pathPointer);
        }
        public void Dispose()
        {
            if (Pointer == IntPtr.Zero) return;
            Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(Pointer, 8));
            Marshal.FreeCoTaskMem(Pointer);
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WinTrustFileInfoNative
        { public uint Size; public IntPtr FilePath; public IntPtr FileHandle; public IntPtr KnownSubject; }
    }

    private sealed class WinTrustData : IDisposable
    {
        public IntPtr Pointer { get; }
        public WinTrustData(WinTrustFileInfo file)
        {
            var native = new WinTrustDataNative
            {
                Size = (uint)Marshal.SizeOf<WinTrustDataNative>(), UIChoice = 2, RevocationChecks = 0,
                UnionChoice = 1, FileInfo = file.Pointer, StateAction = 0, ProvFlags = 0x00001000, UIContext = 0,
            };
            Pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustDataNative>());
            Marshal.StructureToPtr(native, Pointer, false);
        }
        public void Dispose() => Marshal.FreeCoTaskMem(Pointer);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WinTrustDataNative
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
        private int _detached;
        public List<ArmouryTapRecord> Records { get; } = [];
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
            var pipe = CreateTapServer(pipeName);
            await File.WriteAllTextAsync(dllPath + ".config", $"pipe=\\\\.\\pipe\\{pipeName}\ntoken={Convert.ToHexString(token)}\n",
                new UnicodeEncoding(false, true), cancellationToken);
            var remoteModule = Inject(identity, dllPath);
            var holder = new TappedProcess(identity, dllPath, token, pipe, remoteModule, Task.CompletedTask);
            var connectTask = pipe.WaitForConnectionAsync(cancellationToken);
            await connectTask.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid != (uint)identity.ProcessId)
                throw new InvalidOperationException("A tap record pipe connected from an unexpected process.");
            var ready = await ReadRecordAsync(pipe, cancellationToken);
            if (ready is null || ready.ProcessId != identity.ProcessId || ready.Api != 0 || !CryptographicOperations.FixedTimeEquals(ready.Token, token))
                throw new InvalidDataException("The injected tap did not authenticate its ready record.");
            holder._readerTask = holder.ReadLoopAsync();
            return holder;
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
                    if (wire.Api is not 1 and not 2 || !ArmouryTapProtocol.IsRetainableReport(wire.Report)) continue;
                    lock (Records)
                    {
                        if (Records.Count == ArmouryTapProtocol.MaximumRecords) { DroppedRecordCount++; continue; }
                        Records.Add(new(wire.ProcessId, (ArmouryTapApi)wire.Api, wire.Qpc, wire.Result, wire.LastError, wire.Report));
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public async Task DetachAsync()
        {
            if (Interlocked.Exchange(ref _detached, 1) != 0) return;
            Exception? failure = null;
            try
            {
                if (!Revalidate(_identity)) throw new InvalidOperationException("The tapped ASUS process identity changed before hook teardown.");
                StopAndUnload(_identity, _dllPath, _remoteModule);
                _remoteModule = IntPtr.Zero;
            }
            catch (Exception ex) { failure = ex; }
            _readerCancellation.Cancel();
            try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception ex) { failure ??= ex; }
            if (failure is not null) throw new InvalidOperationException("Native hook unload could not be confirmed.", failure);
        }

        public void Dispose() { _readerCancellation.Cancel(); _readerCancellation.Dispose(); _pipe.Dispose(); }

        private sealed record Wire(int ProcessId, byte Api, long Qpc, bool Result, int LastError, byte[] Token, byte[] Report);
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
                BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(12)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)) != 0,
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)), bytes[28..60], bytes[60..(60 + length)]);
        }

        private static NamedPipeServerStream CreateTapServer(string name)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(identity.User!);
            security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, 0, security, HandleInheritability.None);
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
    }

    private static IntPtr Inject(VerifiedProcess identity, string dllPath)
    {
        if (!Revalidate(identity)) throw new InvalidOperationException("Candidate identity failed immediate pre-injection validation.");
        using var process = OpenTarget(identity.ProcessId);
        var bytes = Encoding.Unicode.GetBytes(dllPath + '\0');
        var remotePath = VirtualAllocEx(process, IntPtr.Zero, (nuint)bytes.Length, 0x3000, 0x04);
        if (remotePath == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            if (!WriteProcessMemory(process, remotePath, bytes, bytes.Length, out var written) || written != bytes.Length)
                throw new System.ComponentModel.Win32Exception();
            var loadLibrary = ResolveRemoteProc(identity.ProcessId, "kernel32.dll", "LoadLibraryW");
            RunRemote(process, loadLibrary, remotePath);
            return FindRemoteModule(identity.ProcessId, Path.GetFileName(dllPath));
        }
        finally { VirtualFreeEx(process, remotePath, 0, 0x8000); }
    }

    private static void StopAndUnload(VerifiedProcess identity, string dllPath, IntPtr remoteModule)
    {
        using var process = OpenTarget(identity.ProcessId);
        var stopRva = ReadExportRva(dllPath, "ArmouryTapStop");
        RunRemote(process, remoteModule + checked((int)stopRva), IntPtr.Zero);
        var freeLibrary = ResolveRemoteProc(identity.ProcessId, "kernel32.dll", "FreeLibrary");
        RunRemote(process, freeLibrary, remoteModule);
        if (FindRemoteModule(identity.ProcessId, Path.GetFileName(dllPath), throwIfMissing: false) != IntPtr.Zero)
            throw new InvalidOperationException("The native tap DLL remained loaded after FreeLibrary.");
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
        if (WaitForSingleObject(thread, 15_000) != 0) throw new TimeoutException("Remote tap lifecycle call timed out.");
        if (!GetExitCodeThread(thread, out var exitCode) || exitCode == 0)
            throw new InvalidOperationException("Remote tap lifecycle call failed.");
    }

    private static IntPtr ResolveRemoteProc(int pid, string moduleName, string export)
    {
        var remoteBase = FindRemoteModule(pid, moduleName);
        var localModule = GetModuleHandle(moduleName);
        var localProc = GetProcAddress(localModule, export);
        return remoteBase + checked((int)(localProc.ToInt64() - localModule.ToInt64()));
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ModuleEntry32
    {
        public uint Size; public uint ModuleId; public uint ProcessId; public uint GlobalUsage; public uint ProcessUsage;
        public IntPtr BaseAddress; public uint BaseSize; public IntPtr ModuleHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Module;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExePath;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(SafeProcessHandle process, IntPtr address, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(SafeProcessHandle process, IntPtr address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(SafeProcessHandle process, IntPtr address, byte[] buffer, int size, out int written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle CreateRemoteThread(SafeProcessHandle process, IntPtr attributes, nuint stack, IntPtr start, IntPtr parameter, uint flags, out uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeThread(SafeWaitHandle thread, out uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string module);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Module32First(IntPtr snapshot, ref ModuleEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Module32Next(IntPtr snapshot, ref ModuleEntry32 entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
