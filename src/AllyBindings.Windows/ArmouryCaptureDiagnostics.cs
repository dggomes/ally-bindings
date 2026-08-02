using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AllyBindings.Windows;

internal static partial class ArmouryCaptureDiagnostics
{
    private const int SchemaVersion = 2;
    private const int MaximumBreadcrumbs = 64;
    private const int MaximumErrors = 8;
    private const int MaximumErrorMessageCharacters = 1_024;
    private const int MaximumDiagnosticBytes = 64 * 1_024;
    private const int RetentionCount = 20;
    private static readonly TimeSpan RetentionAge = TimeSpan.FromDays(14);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string GetPath(Guid sessionId) => Path.Combine(
        GetDirectory(),
        $"armoury-etw-{sessionId:D}.json");

    public static string? TryRead(Guid sessionId)
    {
        try
        {
            var path = GetPath(sessionId);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Delete(Guid sessionId)
    {
        try
        {
            var path = GetPath(sessionId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Successful capture cleanup is best-effort.
        }
    }

    public static void Record(
        Guid sessionId,
        string stage,
        Exception? exception = null,
        int? helperExitCode = null)
    {
        try
        {
            var directory = GetDirectory();
            Directory.CreateDirectory(directory);
            using var writeLock = TryAcquireWriteLock(directory, sessionId);
            if (writeLock is null) return;

            var path = GetPath(sessionId);
            var existing = TryReadRecord(path);
            var breadcrumbs = existing?.Breadcrumbs.ToList() ?? [];
            breadcrumbs.Add(new(DateTimeOffset.UtcNow, Environment.ProcessId, Bound(stage, 128)));
            TrimOldest(breadcrumbs, MaximumBreadcrumbs);

            var errors = existing?.Errors.ToList() ?? [];
            if (exception is not null)
            {
                AppendErrors(errors, stage, exception);
                TrimOldest(errors, MaximumErrors);
            }

            var record = new ArmouryCaptureDiagnosticRecord(
                SchemaVersion,
                sessionId,
                DateTimeOffset.UtcNow,
                Bound(stage, 128),
                Environment.ProcessId,
                Environment.IsPrivilegedProcess,
                GetProductVersion(),
                helperExitCode ?? existing?.HelperExitCode,
                "Contains lifecycle stages, process/elevation state, product version, helper exit code, and redacted bounded error types/codes/messages. Contains no usernames, absolute paths, stack traces, USB payloads, controller reports, configuration values, or raw ETW data.",
                breadcrumbs,
                errors);
            WriteAtomicBounded(directory, path, record);
            Prune(directory);
        }
        catch
        {
            // Diagnostics must never change capture safety or lifecycle behavior.
        }
    }

    private static FileStream? TryAcquireWriteLock(string directory, Guid sessionId)
    {
        var lockPath = Path.Combine(directory, $".armoury-etw-{sessionId:N}.lock");
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.1);
        do
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Sleep(10);
            }
        } while (Stopwatch.GetTimestamp() < deadline);
        return null;
    }

    private static void AppendErrors(List<ArmouryCaptureDiagnosticError> errors, string stage, Exception exception)
    {
        for (var current = exception; current is not null && errors.Count < MaximumErrors; current = current.InnerException)
        {
            errors.Add(new(
                DateTimeOffset.UtcNow,
                Environment.ProcessId,
                Bound(stage, 128),
                current.GetType().FullName ?? current.GetType().Name,
                $"0x{current.HResult:X8}",
                Sanitize(current.Message)));
        }
    }

    private static string Sanitize(string value)
    {
        var sanitized = value;
        foreach (var sensitiveRoot in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                 }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sanitized = sanitized.Replace(sensitiveRoot, "<redacted-path>", StringComparison.OrdinalIgnoreCase);
        }
        sanitized = WindowsAbsolutePathRegex().Replace(sanitized, "<redacted-path>");
        return Bound(sanitized, MaximumErrorMessageCharacters);
    }

    private static string Bound(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : string.Concat(value.AsSpan(0, maximumCharacters - 1), "…");

    private static void TrimOldest<T>(List<T> values, int maximum)
    {
        if (values.Count > maximum) values.RemoveRange(0, values.Count - maximum);
    }

    private static string? GetProductVersion()
    {
        var path = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(path) ? null : FileVersionInfo.GetVersionInfo(path).ProductVersion;
    }

    private static string GetDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Windows did not expose the current user's local application-data folder.");
        }
        return Path.Combine(localAppData, "AllyBindings", "diagnostics");
    }

    private static ArmouryCaptureDiagnosticRecord? TryReadRecord(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ArmouryCaptureDiagnosticRecord>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteAtomicBounded(string directory, string path, ArmouryCaptureDiagnosticRecord record)
    {
        var serialized = JsonSerializer.Serialize(record, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(serialized) > MaximumDiagnosticBytes)
        {
            throw new InvalidDataException("The bounded ETW diagnostic exceeded its size ceiling.");
        }

        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, serialized);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Prune(string directory)
    {
        try
        {
            var cutoff = DateTime.UtcNow - RetentionAge;
            var files = new DirectoryInfo(directory)
                .EnumerateFiles("armoury-etw-*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
            foreach (var stale in files.Where((file, index) => index >= RetentionCount || file.LastWriteTimeUtc < cutoff))
            {
                stale.Delete();
            }
        }
        catch
        {
            // Retention cleanup is best-effort and never changes capture behavior.
        }
    }

    [GeneratedRegex(@"(?i)(?:[a-z]:\\|\\\\)[^\r\n\""']+")]
    private static partial Regex WindowsAbsolutePathRegex();
}

internal sealed record ArmouryCaptureDiagnosticRecord(
    int SchemaVersion,
    Guid SessionId,
    DateTimeOffset UpdatedAtUtc,
    string Stage,
    int ProcessId,
    bool IsElevated,
    string? ProductVersion,
    int? HelperExitCode,
    string Privacy,
    IReadOnlyList<ArmouryCaptureDiagnosticBreadcrumb> Breadcrumbs,
    IReadOnlyList<ArmouryCaptureDiagnosticError> Errors);

internal sealed record ArmouryCaptureDiagnosticBreadcrumb(
    DateTimeOffset TimestampUtc,
    int ProcessId,
    string Stage);

internal sealed record ArmouryCaptureDiagnosticError(
    DateTimeOffset TimestampUtc,
    int ProcessId,
    string Stage,
    string Type,
    string HResult,
    string Message);

internal sealed class ArmouryCaptureException : InvalidOperationException
{
    public ArmouryCaptureException(Guid sessionId, string message, Exception innerException)
        : base(message, innerException)
    {
        SessionId = sessionId;
        DiagnosticPath = ArmouryCaptureDiagnostics.GetPath(sessionId);
    }

    public Guid SessionId { get; }
    public string DiagnosticPath { get; }
    public string? DiagnosticText => ArmouryCaptureDiagnostics.TryRead(SessionId);
}

internal sealed class ArmouryCaptureTeardownException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
