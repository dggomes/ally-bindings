using System.Text.RegularExpressions;

namespace AllyBindings.Core;

public sealed record MappingProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public Dictionary<ControllerButton, ControllerButton> Bindings { get; init; } = [];

    public static MappingProfile Default { get; } = new()
    {
        Id = "default",
        Name = "Default",
        Enabled = true,
        Bindings = [],
    };
}

public sealed record ShortcutSettings
{
    public List<ControllerButton> Buttons { get; init; } = [ControllerButton.View, ControllerButton.Menu];
    public int HoldMilliseconds { get; init; } = 250;
    public int CommitDelayMilliseconds { get; init; } = 900;
}

public sealed record AppConfiguration
{
    public int SchemaVersion { get; init; } = 2;
    public string ActiveProfileId { get; init; } = MappingProfile.Default.Id;
    public int? ControllerIndex { get; init; }
    public bool RunAtStartup { get; init; }
    public bool CheckForUpdatesAutomatically { get; init; }
    public bool IncludePrereleaseUpdates { get; init; } = true;
    public DateTimeOffset? LastUpdateCheckUtc { get; init; }
    public bool EnableAsusRearButtonMappings { get; init; }
    public bool AsusRearButtonMappingActive { get; init; }
    public ShortcutSettings Shortcut { get; init; } = new();
    public List<MappingProfile> Profiles { get; init; } = [MappingProfile.Default];

    public static AppConfiguration CreateDefault() => new();
}

public sealed record ConfigurationValidationResult(AppConfiguration Configuration, IReadOnlyList<string> Warnings);

public sealed class UnsupportedConfigurationVersionException(int version)
    : InvalidOperationException($"Configuration schema version {version} is newer than this app supports.")
{
    public int Version { get; } = version;
}

public static partial class ConfigurationValidator
{
    public const int CurrentSchemaVersion = 2;
    private static readonly HashSet<ControllerButton> FaceButtons =
    [ControllerButton.A, ControllerButton.B, ControllerButton.X, ControllerButton.Y];

    public static ConfigurationValidationResult Normalize(AppConfiguration? input)
    {
        input ??= AppConfiguration.CreateDefault();
        if (input.SchemaVersion > CurrentSchemaVersion)
        {
            throw new UnsupportedConfigurationVersionException(input.SchemaVersion);
        }

        var warnings = new List<string>();
        var profiles = new List<MappingProfile> { MappingProfile.Default };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MappingProfile.Default.Id };

        foreach (var profile in input.Profiles ?? [])
        {
            if (profile is null)
            {
                warnings.Add("Skipped an empty profile entry.");
                continue;
            }
            if (string.Equals(profile.Id, MappingProfile.Default.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sourceId = string.IsNullOrWhiteSpace(profile.Id) ? profile.Name : profile.Id;
            var id = Slugify(sourceId);
            if (id.Length == 0 || !seen.Add(id))
            {
                warnings.Add($"Skipped profile '{profile.Name ?? "unnamed"}' because its id is empty or duplicated.");
                continue;
            }

            var name = string.IsNullOrWhiteSpace(profile.Name) ? id : profile.Name.Trim();
            var sourceBindings = profile.Bindings ?? [];
            var bindings = sourceBindings
                .Where(pair => ControllerButtons.IsValidBinding(pair.Key, pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            if (bindings.Count != sourceBindings.Count)
            {
                warnings.Add($"Removed unsupported bindings from profile '{name}'.");
            }
            profiles.Add(profile with { Id = id, Name = name, Bindings = bindings });
        }

        var shortcutButtons = (input.Shortcut?.Buttons ?? [])
            .Where(ControllerButtons.ShortcutButtons.Contains)
            .Distinct()
            .Take(4)
            .ToList();
        if (shortcutButtons.Count < 2)
        {
            warnings.Add("Shortcut needs at least two controller buttons; restored View + Menu.");
            shortcutButtons = [ControllerButton.View, ControllerButton.Menu];
        }
        if (shortcutButtons.All(FaceButtons.Contains))
        {
            warnings.Add("Face-button-only shortcuts can leak gameplay input before safe device hiding is active.");
        }

        var activeProfile = profiles.Any(p => p.Enabled && p.Id.Equals(input.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
            ? input.ActiveProfileId
            : MappingProfile.Default.Id;

        var normalized = input with
        {
            SchemaVersion = CurrentSchemaVersion,
            ActiveProfileId = activeProfile,
            ControllerIndex = input.ControllerIndex is >= 0 and <= 3 ? input.ControllerIndex : null,
            AsusRearButtonMappingActive =
                input.EnableAsusRearButtonMappings && input.AsusRearButtonMappingActive,
            Shortcut = new ShortcutSettings
            {
                Buttons = shortcutButtons,
                HoldMilliseconds = Math.Clamp(input.Shortcut?.HoldMilliseconds ?? 250, 100, 2000),
                CommitDelayMilliseconds = Math.Clamp(input.Shortcut?.CommitDelayMilliseconds ?? 900, 300, 5000),
            },
            Profiles = profiles,
        };

        return new(normalized, warnings);
    }

    public static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var slug = NonSlugCharacters().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length <= 64 ? slug : slug[..64].TrimEnd('-');
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();
}
