using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllyBindings.SoftwareProbe;

public static class SoftwareProbeCheckpoints
{
    public const string ArmouryBaselineSaved = "armoury-baseline-saved";
    public const string F17F18Assigned = "f17-f18-assigned";
    public const string KeyboardCapture = "keyboard-capture";
    public const string VirtualOnlyRemotePlay = "remote-play-virtual-only";
    public const string CoexistenceRemotePlay = "remote-play-coexistence";
    public const string HidHideRequired = "hidhide-required";
    public const string ColdBootPersistence = "cold-boot-persistence";
    public const string ArmouryRestored = "armoury-restored";

    public static readonly ImmutableHashSet<string> Allowed = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        ArmouryBaselineSaved,
        F17F18Assigned,
        KeyboardCapture,
        VirtualOnlyRemotePlay,
        CoexistenceRemotePlay,
        HidHideRequired,
        ColdBootPersistence,
        ArmouryRestored);
}

public enum SoftwareProbeCheckpointResult
{
    Pass,
    Fail,
    Skipped,
    Unknown,
}

public sealed record SoftwareProbeCapabilities(
    string ToolVersion,
    string OperatingSystem,
    string ProductName,
    ImmutableArray<int> ConnectedXInputSlots,
    bool ViGEmBusInstalled,
    bool HidHideInstalled,
    string ViGEmBusStatus,
    string HidHideStatus);

public sealed record SoftwareProbeKeyEvent(
    DateTimeOffset TimestampUtc,
    string Key,
    bool IsKeyDown,
    bool IsInjected,
    bool WasSuppressed,
    string Mode);

public sealed record SoftwareProbeCheckpoint(
    string Name,
    SoftwareProbeCheckpointResult Result,
    DateTimeOffset TimestampUtc,
    string Notes);

public sealed record SoftwareProbeSession(
    int SchemaVersion,
    string SessionId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool Complete,
    string SafetyMode,
    SoftwareProbeCapabilities Capabilities,
    ImmutableArray<SoftwareProbeKeyEvent> KeyEvents,
    ImmutableArray<SoftwareProbeCheckpoint> Checkpoints)
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumKeyEvents = 2_000;
    public const int MaximumNotesLength = 0;
    public const string RequiredSafetyMode = "software-only; no ASUS HID writes; no driver installation; no device hiding";
    private static readonly ImmutableHashSet<string> AllowedEventModes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "capture-observe",
        "capture-suppress",
        "virtual-bridge");

    public static SoftwareProbeSession Create(SoftwareProbeCapabilities capabilities, DateTimeOffset now) => new(
        CurrentSchemaVersion,
        Guid.NewGuid().ToString("N"),
        now,
        now,
        Complete: false,
        SafetyMode: RequiredSafetyMode,
        capabilities,
        [],
        []);

    public SoftwareProbeSession AddKeyEvent(SoftwareProbeKeyEvent keyEvent)
    {
        if (Complete)
            throw new InvalidOperationException("The evidence session is already finalized.");
        if (KeyEvents.Length >= MaximumKeyEvents)
            throw new InvalidOperationException($"The evidence session reached the {MaximumKeyEvents}-event safety limit.");
        if (keyEvent.Key is not ("F17" or "F18"))
            throw new ArgumentOutOfRangeException(nameof(keyEvent), "Only F17/F18 events may be retained.");
        if (keyEvent.TimestampUtc < CreatedAtUtc)
            throw new ArgumentOutOfRangeException(nameof(keyEvent), "Event timestamp predates the session.");
        if (!AllowedEventModes.Contains(keyEvent.Mode))
            throw new ArgumentOutOfRangeException(nameof(keyEvent), "Unknown software-probe event mode.");
        return this with
        {
            UpdatedAtUtc = Later(UpdatedAtUtc, keyEvent.TimestampUtc),
            KeyEvents = KeyEvents.Add(keyEvent).OrderBy(item => item.TimestampUtc).ToImmutableArray(),
        };
    }

    public SoftwareProbeSession SetCheckpoint(
        string name,
        SoftwareProbeCheckpointResult result,
        DateTimeOffset now)
    {
        if (Complete)
            throw new InvalidOperationException("The evidence session is already finalized.");
        if (!SoftwareProbeCheckpoints.Allowed.Contains(name))
            throw new ArgumentOutOfRangeException(nameof(name), "Unknown software-probe checkpoint.");
        if (now < CreatedAtUtc)
            throw new ArgumentOutOfRangeException(nameof(now));

        var checkpoint = new SoftwareProbeCheckpoint(name, result, now, string.Empty);
        return this with
        {
            UpdatedAtUtc = Later(UpdatedAtUtc, now),
            Checkpoints = Checkpoints.RemoveAll(existing => existing.Name == name).Add(checkpoint),
        };
    }

    public SoftwareProbeSession FinalizeSession(DateTimeOffset now)
    {
        if (Complete)
            throw new InvalidOperationException("The evidence session is already finalized.");
        if (now < CreatedAtUtc)
            throw new ArgumentOutOfRangeException(nameof(now));
        var byName = Checkpoints.ToDictionary(checkpoint => checkpoint.Name, StringComparer.Ordinal);
        var missing = SoftwareProbeCheckpoints.Allowed.Where(name => !byName.ContainsKey(name)).OrderBy(name => name).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException($"Record every checkpoint before finalizing. Missing: {string.Join(", ", missing)}.");
        var unknown = byName.Values.Where(checkpoint => checkpoint.Result == SoftwareProbeCheckpointResult.Unknown)
            .Select(checkpoint => checkpoint.Name).OrderBy(name => name).ToArray();
        if (unknown.Length != 0)
            throw new InvalidOperationException($"Resolve or explicitly skip every checkpoint before finalizing. Unknown: {string.Join(", ", unknown)}.");
        if (byName[SoftwareProbeCheckpoints.ArmouryBaselineSaved].Result != SoftwareProbeCheckpointResult.Pass ||
            byName[SoftwareProbeCheckpoints.ArmouryRestored].Result != SoftwareProbeCheckpointResult.Pass)
            throw new InvalidOperationException("Baseline preservation and final Armoury restoration must both pass before finalizing.");

        var finalized = this with { UpdatedAtUtc = Later(UpdatedAtUtc, now), Complete = true };
        finalized.ValidateIntegrity();
        return finalized;
    }

    public void ValidateIntegrity()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported evidence schema {SchemaVersion}.");
        if (!Guid.TryParseExact(SessionId, "N", out _))
            throw new InvalidDataException("The evidence session ID is invalid.");
        if (!string.Equals(SafetyMode, RequiredSafetyMode, StringComparison.Ordinal))
            throw new InvalidDataException("The software-only safety mode is missing or changed.");
        if (UpdatedAtUtc < CreatedAtUtc)
            throw new InvalidDataException("The evidence timestamps are inconsistent.");
        ValidateText(Capabilities.ToolVersion, 100, "tool version");
        ValidateText(Capabilities.OperatingSystem, 300, "operating system");
        ValidateText(Capabilities.ProductName, 300, "product name");
        ValidateText(Capabilities.ViGEmBusStatus, 100, "ViGEmBus status");
        ValidateText(Capabilities.HidHideStatus, 100, "HidHide status");
        if (Capabilities.ConnectedXInputSlots.Any(slot => slot is < 0 or > 3) ||
            Capabilities.ConnectedXInputSlots.Distinct().Count() != Capabilities.ConnectedXInputSlots.Length)
            throw new InvalidDataException("The XInput slot inventory is invalid.");
        if (KeyEvents.Length > MaximumKeyEvents)
            throw new InvalidDataException("The evidence event limit was exceeded.");
        foreach (var keyEvent in KeyEvents)
        {
            if (keyEvent.Key is not ("F17" or "F18") || !AllowedEventModes.Contains(keyEvent.Mode) ||
                keyEvent.TimestampUtc < CreatedAtUtc || keyEvent.TimestampUtc > UpdatedAtUtc)
                throw new InvalidDataException("The evidence contains an invalid key event.");
        }
        if (!IsChronological(KeyEvents.Select(keyEvent => keyEvent.TimestampUtc)))
            throw new InvalidDataException("The evidence key events are not chronological.");
        if (Checkpoints.Length > SoftwareProbeCheckpoints.Allowed.Count ||
            Checkpoints.Select(checkpoint => checkpoint.Name).Distinct(StringComparer.Ordinal).Count() != Checkpoints.Length)
            throw new InvalidDataException("The checkpoint collection is invalid.");
        foreach (var checkpoint in Checkpoints)
        {
            if (!SoftwareProbeCheckpoints.Allowed.Contains(checkpoint.Name) ||
                checkpoint.TimestampUtc < CreatedAtUtc || checkpoint.TimestampUtc > UpdatedAtUtc ||
                checkpoint.Notes.Length > MaximumNotesLength || checkpoint.Notes.Any(char.IsControl))
                throw new InvalidDataException("The evidence contains an invalid checkpoint.");
        }
        if (!IsChronological(Checkpoints.Select(checkpoint => checkpoint.TimestampUtc)))
            throw new InvalidDataException("The evidence checkpoints are not chronological.");
        if (Complete)
        {
            var byName = Checkpoints.ToDictionary(checkpoint => checkpoint.Name, StringComparer.Ordinal);
            if (SoftwareProbeCheckpoints.Allowed.Any(name => !byName.ContainsKey(name)) ||
                byName.Values.Any(checkpoint => checkpoint.Result == SoftwareProbeCheckpointResult.Unknown) ||
                byName[SoftwareProbeCheckpoints.ArmouryBaselineSaved].Result != SoftwareProbeCheckpointResult.Pass ||
                byName[SoftwareProbeCheckpoints.ArmouryRestored].Result != SoftwareProbeCheckpointResult.Pass)
                throw new InvalidDataException("A finalized session is missing decisions or verified Armoury restoration.");
        }
    }

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static bool IsChronological(IEnumerable<DateTimeOffset> timestamps)
    {
        DateTimeOffset? previous = null;
        foreach (var timestamp in timestamps)
        {
            if (previous is not null && timestamp < previous.Value) return false;
            previous = timestamp;
        }
        return true;
    }

    private static void ValidateText(string? value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new InvalidDataException($"The {field} field is invalid.");
    }
}

public sealed record SoftwareProbeBundleResult(string ZipPath, string Sha256, bool Complete);

public static class SoftwareProbeEvidenceStore
{
    public const int MaximumSessionJsonBytes = 4 * 1024 * 1024;
    public const int MaximumBundleBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string CreateSessionDirectory(string root, SoftwareProbeSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Directory.CreateDirectory(root);
        var sessionDirectory = Path.Combine(root, session.SessionId);
        Directory.CreateDirectory(sessionDirectory);
        Save(sessionDirectory, session);
        return sessionDirectory;
    }

    public static SoftwareProbeSession Load(string sessionDirectory)
    {
        using var sessionLock = AcquireExclusive(sessionDirectory, ".session.lock", LockTimeout);
        return LoadUnlocked(sessionDirectory);
    }

    public static void Save(string sessionDirectory, SoftwareProbeSession session)
    {
        Directory.CreateDirectory(sessionDirectory);
        using var sessionLock = AcquireExclusive(sessionDirectory, ".session.lock", LockTimeout);
        SaveUnlocked(sessionDirectory, session);
    }

    public static SoftwareProbeSession Update(
        string sessionDirectory,
        Func<SoftwareProbeSession, SoftwareProbeSession> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        using var sessionLock = AcquireExclusive(sessionDirectory, ".session.lock", LockTimeout);
        var updated = update(LoadUnlocked(sessionDirectory));
        SaveUnlocked(sessionDirectory, updated);
        return updated;
    }

    public static IDisposable AcquireCaptureLease(string sessionDirectory) =>
        AcquireExclusive(sessionDirectory, ".capture.lock", TimeSpan.Zero);

    public static SoftwareProbeBundleResult FinalizeBundle(string sessionDirectory, string? destinationZipPath = null)
    {
        using var captureLease = AcquireExclusive(sessionDirectory, ".capture.lock", TimeSpan.Zero);
        using var sessionLock = AcquireExclusive(sessionDirectory, ".session.lock", LockTimeout);
        var current = LoadUnlocked(sessionDirectory);
        var parent = Path.GetDirectoryName(Path.GetFullPath(sessionDirectory))
            ?? throw new InvalidOperationException("The evidence session has no parent directory.");
        var zipPath = Path.GetFullPath(destinationZipPath ?? Path.Combine(
            parent,
            $"AllyBindings-M1M2-SoftwareProbe-{current.SessionId}.zip"));
        if (!string.Equals(Path.GetExtension(zipPath), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The evidence destination must use the .zip extension.", nameof(destinationZipPath));
        var relativeToSession = Path.GetRelativePath(Path.GetFullPath(sessionDirectory), zipPath);
        if (relativeToSession != ".." && !relativeToSession.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The evidence ZIP cannot overwrite or reside inside its source session.", nameof(destinationZipPath));
        if (File.Exists(zipPath))
            throw new IOException($"The evidence destination already exists: {zipPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        var finalized = current.FinalizeSession(DateTimeOffset.UtcNow);
        var sessionBytes = SerializeSession(finalized);
        var readmeBytes = Encoding.UTF8.GetBytes(
            "Ally Bindings M1/M2 software-probe evidence\r\n" +
            "Collected data is limited to F17/F18 timing, capability status and fixed-choice checkpoint outcomes.\r\n" +
            "It contains no HID packet bytes, broad keyboard history or device-interface paths.\r\n");
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            sessionId = finalized.SessionId,
            complete = finalized.Complete,
            files = new[]
            {
                HashEntry("session.json", sessionBytes),
                HashEntry("README.txt", readmeBytes),
            },
        }, JsonOptions);

        var temporaryZip = zipPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var archive = ZipFile.Open(temporaryZip, ZipArchiveMode.Create))
            {
                WriteZipEntry(archive, "session.json", sessionBytes);
                WriteZipEntry(archive, "README.txt", readmeBytes);
                WriteZipEntry(archive, "manifest.json", manifestBytes);
            }
            if (new FileInfo(temporaryZip).Length > MaximumBundleBytes)
                throw new InvalidDataException($"The evidence bundle exceeds {MaximumBundleBytes} bytes.");
            File.Move(temporaryZip, zipPath);
        }
        finally
        {
            if (File.Exists(temporaryZip)) File.Delete(temporaryZip);
        }
        WriteAtomic(Path.Combine(sessionDirectory, "README.txt"), readmeBytes);
        WriteAtomic(Path.Combine(sessionDirectory, "manifest.json"), manifestBytes);
        WriteAtomic(Path.Combine(sessionDirectory, "session.json"), sessionBytes);
        return new(zipPath, Sha256File(zipPath), finalized.Complete);
    }

    private static SoftwareProbeSession LoadUnlocked(string sessionDirectory)
    {
        var path = Path.Combine(sessionDirectory, "session.json");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The evidence session does not exist.", path);
        if (info.Length > MaximumSessionJsonBytes)
            throw new InvalidDataException($"The evidence session exceeds {MaximumSessionJsonBytes} bytes.");
        var session = JsonSerializer.Deserialize<SoftwareProbeSession>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new InvalidDataException("The evidence session is empty.");
        session.ValidateIntegrity();
        return session;
    }

    private static void SaveUnlocked(string sessionDirectory, SoftwareProbeSession session)
    {
        session.ValidateIntegrity();
        WriteAtomic(Path.Combine(sessionDirectory, "session.json"), SerializeSession(session));
    }

    private static byte[] SerializeSession(SoftwareProbeSession session)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        if (bytes.Length > MaximumSessionJsonBytes)
            throw new InvalidDataException($"The evidence session exceeds {MaximumSessionJsonBytes} bytes.");
        return bytes;
    }

    private static object HashEntry(string name, byte[] bytes) => new
    {
        name,
        bytes = bytes.Length,
        sha256 = Sha256Hex(bytes),
    };

    private static void WriteZipEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static FileStream AcquireExclusive(string sessionDirectory, string fileName, TimeSpan timeout)
    {
        Directory.CreateDirectory(sessionDirectory);
        var path = Path.Combine(sessionDirectory, fileName);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (IOException exception)
            {
                throw new IOException($"Evidence session is busy: {sessionDirectory}", exception);
            }
        }
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
