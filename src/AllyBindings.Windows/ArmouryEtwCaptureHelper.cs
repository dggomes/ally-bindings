using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AllyBindings.Core;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace AllyBindings.Windows;

internal static class ArmouryEtwCaptureHelper
{
    internal const string HelperArgument = "--armoury-etw-capture-helper";
    internal const ulong FullDataTraceKeywords = 0x8101; // Default | FullDataBusTrace | Rundown
    private const int MaximumEventPayloadBytes = 1024 * 1024;
    private const int MaximumRetainedReports = 256;
    private const int MaximumCommandCharacters = 16;
    private const string CaptureSessionName = "AllyBindings-Armoury-Capture";
    private static readonly TimeSpan MaximumCaptureDuration = TimeSpan.FromMinutes(10);
    private static readonly EtwProviderDefinition[] RequiredProviders =
    [
        new("Microsoft-Windows-USB-UCX", new("36DA592D-E43A-4E28-AF6F-4BC57C5A11E8")),
        new("Microsoft-Windows-USB-USBXHCI", new("30E1D284-5D88-459C-83FD-6345B39B19EC")),
        new("Microsoft-Windows-USB-USBHUB3", new("AC52AD17-CC01-4F85-8DF5-4DCE4333C99B")),
    ];
    private static readonly JsonSerializerOptions JsonOptions = new();

    public static bool TryParseArguments(IReadOnlyList<string> args, out Guid sessionId, out int parentProcessId)
    {
        sessionId = Guid.Empty;
        parentProcessId = 0;
        return args.Count == 3 &&
            args[0].Equals(HelperArgument, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(args[1], "D", out sessionId) &&
            int.TryParse(args[2], out parentProcessId) &&
            parentProcessId > 0;
    }

    public static async Task<int> RunAsync(Guid sessionId, int parentProcessId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return 1;

        await using var pipe = new NamedPipeClientStream(
            ".",
            ArmouryEtwCapturePipe.GetPipeName(sessionId),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(15_000, cancellationToken).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverProcessId) ||
                serverProcessId != (uint)parentProcessId)
            {
                throw new InvalidOperationException("The ETW pipe server was not the expected unelevated Ally Bindings parent process.");
            }
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };
            return await CaptureAsync(reader, writer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (pipe.IsConnected)
            {
                try
                {
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
                    {
                        AutoFlush = true,
                    };
                    await WriteEnvelopeAsync(writer, new("error", Error: ex.Message)).ConfigureAwait(false);
                }
                catch
                {
                    // The unelevated parent may already be gone.
                }
            }
            return 1;
        }
    }

    private static async Task<int> CaptureAsync(
        StreamReader reader,
        StreamWriter writer,
        CancellationToken cancellationToken)
    {
        foreach (var provider in RequiredProviders)
        {
            var registered = TraceEventProviders.GetProviderGuidByName(provider.Name);
            if (registered == Guid.Empty || registered != provider.Id)
            {
                throw new InvalidOperationException(
                    $"Required Windows USB ETW provider {provider.Name} ({provider.Id:D}) is unavailable or has an unexpected identity.");
            }
        }

        var reports = new List<UsbEtwFeatureReport>();
        long observedEventCount = 0;
        long oversizedEventCount = 0;
        long payloadDecodeFailureCount = 0;
        long ambiguousCandidateCount = 0;
        long droppedMatchingReportCount = 0;
        // A fixed name lets TraceEvent reclaim a session orphaned by a prior
        // process crash instead of leaking one GUID-named logger per attempt.
        using var session = new TraceEventSession(CaptureSessionName)
        {
            StopOnDispose = true,
            BufferSizeMB = 128,
            BufferQuantumKB = 128,
        };
        session.Source.Dynamic.All += data =>
        {
            observedEventCount++;
            if (data.EventDataLength < 0 || data.EventDataLength > MaximumEventPayloadBytes)
            {
                oversizedEventCount++;
                return;
            }

            try
            {
                var remaining = Math.Max(1, MaximumRetainedReports - reports.Count);
                var extraction = UsbEtwHidFeatureReportExtractor.Extract(
                    new DateTimeOffset(session.Source.SessionStartTime)
                        .AddMilliseconds(data.TimeStampRelativeMSec)
                        .ToUniversalTime(),
                    data.ProviderName ?? data.ProviderGuid.ToString("D"),
                    data.EventName ?? $"event-{data.ID}",
                    (int)data.ID,
                    GetBinaryFields(data),
                    remaining);
                foreach (var report in extraction.Reports)
                {
                    if (reports.Count >= MaximumRetainedReports)
                    {
                        droppedMatchingReportCount++;
                        break;
                    }
                    reports.Add(report);
                }
                if (extraction.LimitExceeded)
                {
                    droppedMatchingReportCount++;
                }
                ambiguousCandidateCount += extraction.AmbiguousCandidateCount;
            }
            catch
            {
                payloadDecodeFailureCount++;
            }
        };

        var enabledProviders = new List<EtwProviderStatus>();
        foreach (var provider in RequiredProviders)
        {
            // EnableProvider returns whether an existing session had to be
            // restarted, not whether enabling succeeded. Failure is an exception.
            session.EnableProvider(provider.Id, TraceEventLevel.Verbose, FullDataTraceKeywords);
            enabledProviders.Add(new(provider.Name, provider.Id, FullDataTraceKeywords));
        }

        var processing = Task.Run(session.Source.Process, CancellationToken.None);
        await WriteEnvelopeAsync(writer, new("ready", Ready: new(enabledProviders))).ConfigureAwait(false);

        string? command = null;
        Exception? commandFailure = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(MaximumCaptureDuration);
        try
        {
            command = await ReadBoundedLineAsync(reader, MaximumCommandCharacters, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            commandFailure = new TimeoutException("The integrated USB ETW capture exceeded its 10-minute safety limit.", ex);
        }
        catch (Exception ex)
        {
            commandFailure = ex;
        }
        finally
        {
            session.Stop();
            await processing.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        }

        if (commandFailure is not null) throw commandFailure;
        var cancelled = command is null || command.Equals("cancel", StringComparison.Ordinal);
        if (command is not null && !command.Equals("cancel", StringComparison.Ordinal) &&
            !command.Equals("stop", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The ETW helper received an invalid parent command.");
        }
        if (cancelled) return 2;

        await WriteEnvelopeAsync(
            writer,
            new(
                "result",
                Output: new(
                    enabledProviders,
                    observedEventCount,
                    session.EventsLost,
                    oversizedEventCount,
                    payloadDecodeFailureCount,
                    ambiguousCandidateCount,
                    droppedMatchingReportCount,
                    reports))).ConfigureAwait(false);
        return 0;
    }

    private static IReadOnlyList<UsbEtwBinaryField> GetBinaryFields(TraceEvent data)
    {
        var fields = new List<UsbEtwBinaryField>();
        var names = data.PayloadNames;
        for (var index = 0; index < names.Length; index++)
        {
            var value = data.PayloadValue(index);
            switch (value)
            {
                case byte[] bytes:
                    fields.Add(new(names[index], bytes));
                    break;
                case ArraySegment<byte> segment:
                    fields.Add(new(names[index], segment.ToArray()));
                    break;
                case ReadOnlyMemory<byte> memory:
                    fields.Add(new(names[index], memory.ToArray()));
                    break;
                case Memory<byte> memory:
                    fields.Add(new(names[index], memory.ToArray()));
                    break;
            }
        }
        return fields;
    }

    private static async Task<string?> ReadBoundedLineAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(maximumCharacters, 64));
        var buffer = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return result.Length == 0 ? null : result.ToString();
            if (buffer[0] == '\n') return result.ToString().TrimEnd('\r');
            if (result.Length == maximumCharacters)
            {
                throw new InvalidDataException("The ETW helper command exceeded its maximum length.");
            }
            result.Append(buffer[0]);
        }
    }

    private static Task WriteEnvelopeAsync(StreamWriter writer, EtwPipeEnvelope envelope) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(envelope, JsonOptions));

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint serverProcessId);
}

internal static class ArmouryEtwCapturePipe
{
    internal const string ResultFileName = "etw-filtered-reports.json";

    public static string GetPipeName(Guid sessionId) => $"AllyBindings.ArmouryEtw.{sessionId:D}";

    public static string GetCaptureRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Windows did not expose the current user's local application-data folder.");
        }
        return Path.Combine(localAppData, "AllyBindings", "captures");
    }
}

internal sealed record EtwProviderDefinition(string Name, Guid Id);
internal sealed record EtwProviderStatus(string Name, Guid Id, ulong KeywordMask);
internal sealed record EtwPipeEnvelope(
    string Type,
    EtwCaptureReady? Ready = null,
    EtwCaptureOutput? Output = null,
    string? Error = null);
internal sealed record EtwCaptureReady(IReadOnlyList<EtwProviderStatus> EnabledProviders);
internal sealed record EtwCaptureOutput(
    IReadOnlyList<EtwProviderStatus> EnabledProviders,
    long ObservedEventCount,
    long EventsLost,
    long OversizedEventCount,
    long PayloadDecodeFailureCount,
    long AmbiguousCandidateCount,
    long DroppedMatchingReportCount,
    IReadOnlyList<UsbEtwFeatureReport> Reports);
