using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllyBindings.Core;

public sealed record DiagnosticsSnapshot(
    string AppVersion,
    string OsVersion,
    string Architecture,
    DateTimeOffset GeneratedAt,
    BackendStatus Backend,
    int ProfileCount,
    string ActiveProfileId,
    int? ControllerIndex,
    IReadOnlyList<string> ConfigurationWarnings);

public static class DiagnosticsExporter
{
    public static string ToJson(DiagnosticsSnapshot snapshot) => JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    });
}
