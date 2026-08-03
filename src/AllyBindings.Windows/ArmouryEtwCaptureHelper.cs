using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
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
    private const int MaximumSchemaShapes = 256;
    private const int MaximumSchemaShapesPerPhase = 64;
    private const int MaximumPrioritySchemaShapes = 64;
    private const int MaximumPrioritySchemaShapesPerPhase = 16;
    private const int MaximumFramingSchemaShapes = MaximumSchemaShapes - MaximumPrioritySchemaShapes;
    private const int MaximumFramingSchemaShapesPerPhase = MaximumSchemaShapesPerPhase - MaximumPrioritySchemaShapesPerPhase;
    private const int MaximumMarkerShapes = 64;
    private const int MaximumMarkerShapesPerPhase = 16;
    private const int MaximumPayloadProperties = 256;
    private const int MaximumDecodedPayloadProperties = 256;
    private const int MaximumPayloadNestingDepth = 8;
    private const int MaximumVisitedPayloadNodes = 1_024;
    private const int MaximumMetadataCharacters = 64;
    private const int MaximumSchemaDiscoveryBytes = 128 * 1024;
    private const long MaximumObservedEvents = 2_000_000;
    private const long MaximumDecodedBinaryBytes = 512L * 1024 * 1024;

    private const int ProviderEnableTimeoutMilliseconds = 10_000;
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

        ArmouryCaptureDiagnostics.Record(sessionId, "helper-started");
        await using var pipe = new NamedPipeClientStream(
            ".",
            ArmouryEtwCapturePipe.GetPipeName(sessionId),
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(15_000, cancellationToken).ConfigureAwait(false);
            ArmouryCaptureDiagnostics.Record(sessionId, "helper-pipe-connected");
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverProcessId) ||
                serverProcessId != (uint)parentProcessId)
            {
                throw new InvalidOperationException("The ETW pipe server was not the expected unelevated Ally Bindings parent process.");
            }
            VerifyParentExecutableIdentity(parentProcessId);
            ArmouryCaptureDiagnostics.Record(sessionId, "helper-parent-authenticated");
            using var reader = new BoundedTextLineReader(
                new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true));
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };
            return await CaptureAsync(sessionId, reader, writer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ArmouryCaptureDiagnostics.Record(sessionId, "helper-failed", ex, helperExitCode: 1);
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
        Guid sessionId,
        BoundedTextLineReader reader,
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
        ArmouryCaptureDiagnostics.Record(sessionId, "helper-providers-verified");

        var reports = new List<UsbEtwFeatureReport>();
        var schemaShapes = new UsbEtwPrioritizedSchemaCounter<EtwSchemaShapeKey>(
            static item => item.Phase,
            MaximumPrioritySchemaShapes,
            MaximumPrioritySchemaShapesPerPhase,
            MaximumFramingSchemaShapes,
            MaximumFramingSchemaShapesPerPhase);
        var markerShapes = new Dictionary<EtwMarkerShapeKey, long>();
        var capturePhases = new UsbEtwCapturePhaseWindows();
        long observedEventCount = 0;
        long oversizedEventCount = 0;
        long payloadDecodeFailureCount = 0;
        long ambiguousCandidateCount = 0;
        long droppedMatchingReportCount = 0;
        long decodedBinaryByteCount = 0;
        var aggregateLimitExceeded = false;
        var schemaDiscoveryLimitExceeded = false;
        // A fixed name lets TraceEvent reclaim a session orphaned by a prior
        // process crash instead of leaking one GUID-named logger per attempt.
        using var session = new TraceEventSession(CaptureSessionName)
        {
            StopOnDispose = true,
            BufferSizeMB = 128,
            BufferQuantumKB = 128,
            EnableProviderTimeoutMSec = ProviderEnableTimeoutMilliseconds,
        };
        ArmouryCaptureDiagnostics.Record(sessionId, "helper-session-created");
        session.Source.Dynamic.All += data =>
        {
            observedEventCount++;
            if (observedEventCount > MaximumObservedEvents || aggregateLimitExceeded)
            {
                aggregateLimitExceeded = true;
                return;
            }
            if (data.EventDataLength < 0 || data.EventDataLength > MaximumEventPayloadBytes)
            {
                oversizedEventCount++;
                return;
            }

            try
            {
                var fields = GetEventFields(data, MaximumDecodedBinaryBytes - decodedBinaryByteCount);
                decodedBinaryByteCount += fields.DecodedBytes;
                if (fields.AggregateLimitExceeded)
                {
                    aggregateLimitExceeded = true;
                    return;
                }
                if (fields.DiscoveryLimitExceeded)
                {
                    schemaDiscoveryLimitExceeded = true;
                }

                var providerName = NormalizeMetadata(
                    data.ProviderName ?? data.ProviderGuid.ToString("D"),
                    "unknown-provider");
                var eventName = NormalizeMetadata(data.EventName, $"event-{data.ID}");
#pragma warning disable CS0618 // Raw ETW QPC is required to classify buffered events against parent QPC boundaries.
                var eventQpc = data.TimeStampQPC;
#pragma warning restore CS0618
                var phase = capturePhases.Classify(eventQpc);
                // V13 proved that binary-only retention is still too broad: firmware hashes,
                // configuration descriptors and command TRBs consumed the inventory while
                // the scalar-only UCX URB framing disappeared. Retain only metadata for UCX
                // class/control-transfer bodies, status and data fields. Exact marker and
                // report inspection below still considers every decoded field and byte-array
                // leaf in memory.
                foreach (var field in fields.PropertyShapes)
                {
                    var retentionClass = UsbEtwSchemaRetentionPolicy.Classify(providerName, eventName, field.Name);
                    if (retentionClass == UsbEtwSchemaRetentionClass.None) continue;
                    var key = new EtwSchemaShapeKey(
                        phase,
                        providerName,
                        eventName,
                        (int)data.ID,
                        (int)data.Version,
                        (int)data.Opcode,
                        fields.PayloadPropertyCountBucket,
                        field.Ordinal,
                        field.Name,
                        field.RuntimeType,
                        field.LengthBucket,
                        fields.TotalBinaryLengthBucket);
                    if (!schemaShapes.Increment(key, retentionClass))
                    {
                        schemaDiscoveryLimitExceeded = true;
                    }
                }

                try
                {
                    foreach (var observation in UsbEtwSchemaDiscovery.Inspect(
                        fields.DiscoveryFields,
                        MaximumMarkerShapes))
                    {
                        var start = fields.PropertyShapes.Single(item => item.Ordinal == observation.StartFieldOrdinal);
                        var end = fields.PropertyShapes.Single(item => item.Ordinal == observation.EndFieldOrdinal);
                        var key = new EtwMarkerShapeKey(
                            phase,
                            providerName,
                            eventName,
                            (int)data.ID,
                            (int)data.Version,
                            (int)data.Opcode,
                            observation.Kind.ToString(),
                            start.Ordinal,
                            end.Ordinal,
                            start.Name,
                            end.Name,
                            start.RuntimeType,
                            end.RuntimeType,
                            start.LengthBucket,
                            end.LengthBucket,
                            BucketLength(observation.StartOffset),
                            BucketLength(observation.BytesAvailableAfterMarker));
                        IncrementPhaseBounded(
                            markerShapes,
                            key,
                            static item => item.Phase,
                            MaximumMarkerShapes,
                            MaximumMarkerShapesPerPhase,
                            ref schemaDiscoveryLimitExceeded);
                    }
                }
                catch (UsbEtwSchemaDiscoveryLimitException)
                {
                    schemaDiscoveryLimitExceeded = true;
                }

                var remaining = Math.Max(1, MaximumRetainedReports - reports.Count);
                var extraction = UsbEtwHidFeatureReportExtractor.Extract(
                    new DateTimeOffset(session.Source.SessionStartTime)
                        .AddMilliseconds(data.TimeStampRelativeMSec)
                        .ToUniversalTime(),
                    providerName,
                    eventName,
                    (int)data.ID,
                    fields.BinaryFields,
                    remaining,
                    eventQpc);
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
        ArmouryCaptureDiagnostics.Record(sessionId, "helper-providers-enabled");

        var processing = Task.Run(session.Source.Process, CancellationToken.None);
        await WriteEnvelopeAsync(writer, new("ready", Ready: new(enabledProviders))).ConfigureAwait(false);
        ArmouryCaptureDiagnostics.Record(sessionId, "helper-ready-sent");

        string? command = null;
        Exception? commandFailure = null;
        long eventsLost = 0;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(MaximumCaptureDuration);
        try
        {
            while (true)
            {
                command = await reader.ReadLineAsync(
                    UsbEtwCapturePhaseCommand.MaximumCommandCharacters,
                    lifetime.Token).ConfigureAwait(false);
                if (command is null)
                {
                    ArmouryCaptureDiagnostics.Record(sessionId, "helper-command-disconnected");
                    break;
                }
                if (command is "stop" or "cancel")
                {
                    ArmouryCaptureDiagnostics.Record(sessionId, $"helper-command-{command}");
                    break;
                }
                if (!UsbEtwCapturePhaseCommand.TryParse(command, out var phase, out var transition))
                {
                    throw new InvalidDataException("The ETW helper received an invalid phase command.");
                }
                var boundaryQpc = transition == UsbEtwCapturePhaseTransition.Start
                    ? capturePhases.StartNow(phase)
                    : capturePhases.EndNow(phase);
                ArmouryCaptureDiagnostics.Record(
                    sessionId,
                    $"helper-command-{transition.ToString().ToLowerInvariant()}-{phase}");
                await WriteEnvelopeAsync(
                    writer,
                    new(
                        "phase-ack",
                        Phase: phase,
                        PhaseStarted: transition == UsbEtwCapturePhaseTransition.Start,
                        BoundaryQpc: boundaryQpc)).ConfigureAwait(false);
            }
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
            try
            {
                // EventsLost queries the live named session. Snapshot it before
                // Stop destroys that session; querying afterwards throws
                // ERROR_WMI_INSTANCE_NOT_FOUND in TraceEvent 3.2.5.
                session.Flush();
                eventsLost = session.EventsLost;
            }
            finally
            {
                session.Stop();
                await processing.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
        }
        ArmouryCaptureDiagnostics.Record(sessionId, "helper-session-stopped");

        if (commandFailure is not null) throw commandFailure;
        var cancelled = command is null || command.Equals("cancel", StringComparison.Ordinal);
        if (command is not null && !command.Equals("cancel", StringComparison.Ordinal) &&
            !command.Equals("stop", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The ETW helper received an invalid parent command.");
        }
        if (cancelled) return 2;

        var schemaShapeOutput = schemaShapes.Entries
            .OrderBy(pair => pair.Key.Phase)
            .ThenBy(pair => pair.Key.ProviderName, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.EventName, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.EventId)
            .ThenBy(pair => pair.Key.EventVersion)
            .ThenBy(pair => pair.Key.Opcode)
            .ThenBy(pair => pair.Key.PayloadPropertyCountBucket, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.FieldOrdinal)
            .ThenBy(pair => pair.Key.FieldName, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.RuntimeType, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.FieldLengthBucket, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.TotalBinaryLengthBucket, StringComparer.Ordinal)
            .Select(pair => new UsbEtwSchemaShape(
                pair.Key.Phase,
                pair.Key.ProviderName,
                pair.Key.EventName,
                pair.Key.EventId,
                pair.Key.EventVersion,
                pair.Key.Opcode,
                pair.Key.PayloadPropertyCountBucket,
                pair.Key.FieldOrdinal,
                pair.Key.FieldName,
                pair.Key.RuntimeType,
                pair.Key.FieldLengthBucket,
                pair.Key.TotalBinaryLengthBucket,
                pair.Value))
            .ToList();
        var markerShapeOutput = markerShapes
            .OrderBy(pair => pair.Key.Phase)
            .ThenBy(pair => pair.Key.ProviderName, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.EventId)
            .ThenBy(pair => pair.Key.Kind, StringComparer.Ordinal)
            .Select(pair => new UsbEtwMarkerShape(
                pair.Key.Phase,
                pair.Key.ProviderName,
                pair.Key.EventName,
                pair.Key.EventId,
                pair.Key.EventVersion,
                pair.Key.Opcode,
                pair.Key.Kind,
                pair.Key.StartFieldOrdinal,
                pair.Key.EndFieldOrdinal,
                pair.Key.StartFieldName,
                pair.Key.EndFieldName,
                pair.Key.StartRuntimeType,
                pair.Key.EndRuntimeType,
                pair.Key.StartLengthBucket,
                pair.Key.EndLengthBucket,
                pair.Key.StartOffsetBucket,
                pair.Key.BytesAfterMarkerBucket,
                pair.Value))
            .ToList();
        var discoveryBytes = JsonSerializer.SerializeToUtf8Bytes(
            new { SchemaShapes = schemaShapeOutput, MarkerShapes = markerShapeOutput },
            JsonOptions);
        if (discoveryBytes.Length > MaximumSchemaDiscoveryBytes)
        {
            throw new InvalidDataException("The bounded ETW schema-discovery result exceeded its serialization limit.");
        }

        await WriteEnvelopeAsync(
            writer,
            new(
                "result",
                Output: new(
                    enabledProviders,
                    observedEventCount,
                    eventsLost,
                    oversizedEventCount,
                    payloadDecodeFailureCount,
                    ambiguousCandidateCount,
                    droppedMatchingReportCount,
                    decodedBinaryByteCount,
                    aggregateLimitExceeded,
                    schemaDiscoveryLimitExceeded,
                    reports,
                    schemaShapeOutput,
                    markerShapeOutput))).ConfigureAwait(false);
        ArmouryCaptureDiagnostics.Record(sessionId, "helper-result-sent");
        return 0;
    }

    private static EtwEventFieldExtraction GetEventFields(TraceEvent data, long remainingByteBudget)
    {
        var binaryFields = new List<UsbEtwBinaryField>();
        var discoveryFields = new List<UsbEtwDiscoveryField>();
        var propertyShapes = new List<EtwPropertyShape>();
        long decodedBytes = 0;
        long totalBinaryLength = 0;
        var names = data.PayloadNames;
        if (names.Length > MaximumDecodedPayloadProperties)
        {
            return new(
                binaryFields,
                discoveryFields,
                propertyShapes,
                decodedBytes,
                BucketLength(names.Length),
                BucketLength(totalBinaryLength),
                AggregateLimitExceeded: true,
                DiscoveryLimitExceeded: true);
        }

        var topLevelFields = new List<KeyValuePair<string, object?>>(names.Length);
        for (var index = 0; index < names.Length; index++)
        {
            topLevelFields.Add(new(names[index], data.PayloadValue(index)));
        }
        var flattened = UsbEtwPayloadFlattener.Flatten(
            topLevelFields,
            MaximumDecodedPayloadProperties,
            MaximumPayloadNestingDepth,
            MaximumVisitedPayloadNodes);
        var discoveryLimitExceeded =
            flattened.LimitExceeded ||
            flattened.Fields.Count > MaximumPayloadProperties;

        for (var index = 0; index < flattened.Fields.Count; index++)
        {
            var value = flattened.Fields[index].Value;
            var propertyName = NormalizeMetadata(flattened.Fields[index].Name, $"field-{index}");
            var binary = ToBinaryBytes(value);
            if (binary is not null)
            {
                if (binary.LongLength > remainingByteBudget - decodedBytes)
                {
                    return new(
                        binaryFields,
                        discoveryFields,
                        propertyShapes,
                        decodedBytes,
                        BucketLength(names.Length),
                        BucketLength(totalBinaryLength),
                        AggregateLimitExceeded: true,
                        DiscoveryLimitExceeded: discoveryLimitExceeded);
                }
                binaryFields.Add(new(propertyName, binary));
                decodedBytes += binary.LongLength;
                totalBinaryLength += binary.LongLength;
            }

            if (index >= MaximumPayloadProperties) continue;
            var runtimeType = GetRuntimeType(value);
            var observedLength = GetObservedLength(value, binary);
            propertyShapes.Add(new(
                index,
                propertyName,
                runtimeType,
                BucketLength(observedLength)));

            var comparable = binary ?? value switch
            {
                byte scalarByte => [scalarByte],
                sbyte scalarSByte => [unchecked((byte)scalarSByte)],
                _ => null,
            };
            discoveryFields.Add(new(
                index,
                propertyName,
                runtimeType,
                observedLength,
                comparable ?? []));
        }

        return new(
            binaryFields,
            discoveryFields,
            propertyShapes,
            decodedBytes,
            BucketLength(names.Length),
            BucketLength(totalBinaryLength),
            AggregateLimitExceeded: false,
            DiscoveryLimitExceeded: discoveryLimitExceeded);
    }

    private static byte[]? ToBinaryBytes(object? value) => value switch
    {
        byte[] bytes => bytes,
        ArraySegment<byte> segment => segment.ToArray(),
        ReadOnlyMemory<byte> memory => memory.ToArray(),
        Memory<byte> memory => memory.ToArray(),
        _ => null,
    };

    private static string GetRuntimeType(object? value) => value switch
    {
        null => "Null",
        byte[] or ArraySegment<byte> or ReadOnlyMemory<byte> or Memory<byte> => "ByteArray",
        byte => "Byte",
        sbyte => "SByte",
        ushort => "UInt16",
        short => "Int16",
        uint => "UInt32",
        int => "Int32",
        ulong => "UInt64",
        long => "Int64",
        string => "String",
        Guid => "Guid",
        Array => "OtherArray",
        _ => "Other",
    };

    private static int GetObservedLength(object? value, byte[]? binary) => value switch
    {
        null => 0,
        _ when binary is not null => binary.Length,
        string text => text.Length,
        Array array => array.Length,
        byte or sbyte => 1,
        ushort or short => 2,
        uint or int => 4,
        ulong or long => 8,
        Guid => 16,
        _ => 0,
    };

    private static string BucketLength(long value) => value switch
    {
        < 0 => "invalid",
        <= 64 => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        <= 128 => "65-128",
        <= 256 => "129-256",
        <= 1024 => "257-1024",
        _ => ">1024",
    };

    private static void IncrementPhaseBounded<TKey>(
        Dictionary<TKey, long> values,
        TKey key,
        Func<TKey, int> getPhase,
        int maximumKeys,
        int maximumKeysPerPhase,
        ref bool limitExceeded)
        where TKey : notnull
    {
        if (values.TryGetValue(key, out var count))
        {
            values[key] = count == long.MaxValue ? long.MaxValue : count + 1;
            return;
        }
        if (values.Count == maximumKeys ||
            values.Keys.Count(existing => getPhase(existing) == getPhase(key)) == maximumKeysPerPhase)
        {
            limitExceeded = true;
            return;
        }
        values.Add(key, 1);
    }

    private static string NormalizeMetadata(string? value, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var length = Math.Min(source.Length, MaximumMetadataCharacters);
        return string.Create(length, source, static (destination, input) =>
        {
            for (var index = 0; index < destination.Length; index++)
            {
                var character = input[index];
                destination[index] =
                    (character is >= 'a' and <= 'z') ||
                    (character is >= 'A' and <= 'Z') ||
                    (character is >= '0' and <= '9') ||
                    character is '_' or '-' or '.' or ':'
                        ? character
                        : '_';
            }
        });
    }

    private static void VerifyParentExecutableIdentity(int parentProcessId)
    {
        var helperPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Windows did not expose the elevated helper executable path.");
        using var parent = Process.GetProcessById(parentProcessId);
        var parentPath = parent.MainModule?.FileName
            ?? throw new InvalidOperationException("Windows did not expose the ETW pipe server executable path.");
        if (!Path.GetFullPath(parentPath).Equals(Path.GetFullPath(helperPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The ETW pipe server is not the same Ally Bindings executable as the elevated helper.");
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

    public static NamedPipeServerStream CreateServer(Guid sessionId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Armoury ETW pipe ACL is available only on Windows.");
        }

        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User
            ?? throw new InvalidOperationException("Windows did not expose the current user's security identifier.");
        var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, domainSid: null);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(userSid);
        security.AddAccessRule(new(
            networkSid,
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        security.AddAccessRule(new(
            userSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // CurrentUserOnly intentionally cannot be used here: on Windows it also
        // requires the client and server to have the same UAC elevation level.
        // The helper is elevated and the app is not. This explicit SID ACL keeps
        // the pipe local/current-user while PID + executable checks authenticate
        // both endpoints after connection.
        return NamedPipeServerStreamAcl.Create(
            GetPipeName(sessionId),
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security,
            HandleInheritability.None,
            additionalAccessRights: default);
    }

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
internal sealed record EtwEventFieldExtraction(
    IReadOnlyList<UsbEtwBinaryField> BinaryFields,
    IReadOnlyList<UsbEtwDiscoveryField> DiscoveryFields,
    IReadOnlyList<EtwPropertyShape> PropertyShapes,
    long DecodedBytes,
    string PayloadPropertyCountBucket,
    string TotalBinaryLengthBucket,
    bool AggregateLimitExceeded,
    bool DiscoveryLimitExceeded);
internal sealed record EtwPropertyShape(
    int Ordinal,
    string Name,
    string RuntimeType,
    string LengthBucket);
internal sealed record EtwPipeEnvelope(
    string Type,
    EtwCaptureReady? Ready = null,
    EtwCaptureOutput? Output = null,
    string? Error = null,
    string? ErrorCode = null,
    int? Phase = null,
    bool? PhaseStarted = null,
    long? BoundaryQpc = null);
internal sealed record EtwCaptureReady(IReadOnlyList<EtwProviderStatus> EnabledProviders);
internal sealed record EtwCaptureOutput(
    IReadOnlyList<EtwProviderStatus> EnabledProviders,
    long ObservedEventCount,
    long EventsLost,
    long OversizedEventCount,
    long PayloadDecodeFailureCount,
    long AmbiguousCandidateCount,
    long DroppedMatchingReportCount,
    long DecodedBinaryByteCount,
    bool AggregateLimitExceeded,
    bool SchemaDiscoveryLimitExceeded,
    IReadOnlyList<UsbEtwFeatureReport> Reports,
    IReadOnlyList<UsbEtwSchemaShape> SchemaShapes,
    IReadOnlyList<UsbEtwMarkerShape> MarkerShapes,
    IReadOnlyList<ArmouryTapRecord>? TapRecords = null,
    IReadOnlyList<ArmouryTapPreFilterDiagnostics>? TapDiagnostics = null);
internal sealed record EtwSchemaShapeKey(
    int Phase,
    string ProviderName,
    string EventName,
    int EventId,
    int EventVersion,
    int Opcode,
    string PayloadPropertyCountBucket,
    int FieldOrdinal,
    string FieldName,
    string RuntimeType,
    string FieldLengthBucket,
    string TotalBinaryLengthBucket);

internal sealed record EtwMarkerShapeKey(
    int Phase,
    string ProviderName,
    string EventName,
    int EventId,
    int EventVersion,
    int Opcode,
    string Kind,
    int StartFieldOrdinal,
    int EndFieldOrdinal,
    string StartFieldName,
    string EndFieldName,
    string StartRuntimeType,
    string EndRuntimeType,
    string StartLengthBucket,
    string EndLengthBucket,
    string StartOffsetBucket,
    string BytesAfterMarkerBucket);
