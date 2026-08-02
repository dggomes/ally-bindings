using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AllyBindings.Windows;

internal static class ArmouryCaptureDiagnostics
{
    private const int SchemaVersion = 1;
    private const int MaximumBreadcrumbs = 64;
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

    public static void Record(
        Guid sessionId,
        string stage,
        Exception? exception = null,
        int? helperExitCode = null)
    {
        try
        {
            using var mutex = new Mutex(false, $@"Local\AllyBindings.ArmouryDiagnostic.{sessionId:N}");
            var lockTaken = false;
            try
            {
                try
                {
                    lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(2));
                }
                catch (AbandonedMutexException)
                {
                    lockTaken = true;
                }
                if (!lockTaken) return;

                var directory = GetDirectory();
                Directory.CreateDirectory(directory);
                var path = GetPath(sessionId);
                var existing = TryReadRecord(path);
                var breadcrumbs = existing?.Breadcrumbs.ToList() ?? [];
                breadcrumbs.Add(new(DateTimeOffset.UtcNow, Environment.ProcessId, stage));
                if (breadcrumbs.Count > MaximumBreadcrumbs)
                {
                    breadcrumbs.RemoveRange(0, breadcrumbs.Count - MaximumBreadcrumbs);
                }

                var record = new ArmouryCaptureDiagnosticRecord(
                    SchemaVersion,
                    sessionId,
                    DateTimeOffset.UtcNow,
                    stage,
                    Environment.ProcessId,
                    Environment.IsPrivilegedProcess,
                    Environment.ProcessPath,
                    FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? string.Empty).ProductVersion,
                    helperExitCode,
                    exception?.GetType().FullName,
                    exception?.Message,
                    exception?.ToString(),
                    "No USB payloads, controller reports, configuration values, or raw ETW data are written to this diagnostic.",
                    breadcrumbs);
                var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllText(temporary, JsonSerializer.Serialize(record, JsonOptions));
                    File.Move(temporary, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                if (stage.Equals("parent-capture-starting", StringComparison.Ordinal))
                {
                    Prune(directory, keep: 20);
                }
            }
            finally
            {
                if (lockTaken) mutex.ReleaseMutex();
            }
        }
        catch
        {
            // Diagnostics must never change capture safety or lifecycle behavior.
        }
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

    private static void Prune(string directory, int keep)
    {
        try
        {
            foreach (var stale in new DirectoryInfo(directory)
                         .EnumerateFiles("armoury-etw-*.json", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(keep))
            {
                stale.Delete();
            }
        }
        catch
        {
            // Retention cleanup is best-effort and never changes capture behavior.
        }
    }
}

internal sealed record ArmouryCaptureDiagnosticRecord(
    int SchemaVersion,
    Guid SessionId,
    DateTimeOffset UpdatedAtUtc,
    string Stage,
    int ProcessId,
    bool IsElevated,
    string? ExecutablePath,
    string? ProductVersion,
    int? HelperExitCode,
    string? ErrorType,
    string? ErrorMessage,
    string? Exception,
    string Privacy,
    IReadOnlyList<ArmouryCaptureDiagnosticBreadcrumb> Breadcrumbs);

internal sealed record ArmouryCaptureDiagnosticBreadcrumb(
    DateTimeOffset TimestampUtc,
    int ProcessId,
    string Stage);

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
