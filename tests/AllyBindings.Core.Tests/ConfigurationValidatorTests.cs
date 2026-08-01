using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class ConfigurationValidatorTests
{
    [Fact]
    public void Future_schema_is_rejected_instead_of_downgraded()
    {
        var configuration = AppConfiguration.CreateDefault() with { SchemaVersion = 99 };

        var exception = Assert.Throws<UnsupportedConfigurationVersionException>(() => ConfigurationValidator.Normalize(configuration));

        Assert.Equal(99, exception.Version);
    }

    [Fact]
    public void Normalize_guarantees_default_profile_and_safe_shortcut()
    {
        var result = ConfigurationValidator.Normalize(new AppConfiguration
        {
            ActiveProfileId = "missing",
            Shortcut = new ShortcutSettings { Buttons = [ControllerButton.A] },
            Profiles = [],
        });

        Assert.Equal("default", result.Configuration.ActiveProfileId);
        Assert.Equal("default", result.Configuration.Profiles[0].Id);
        Assert.Equal([ControllerButton.View, ControllerButton.Menu], result.Configuration.Shortcut.Buttons);
        Assert.Contains(result.Warnings, warning => warning.Contains("at least two", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Normalize_warns_about_face_button_only_chord()
    {
        var result = ConfigurationValidator.Normalize(new AppConfiguration
        {
            Shortcut = new ShortcutSettings { Buttons = [ControllerButton.A, ControllerButton.B] },
        });

        Assert.Equal([ControllerButton.A, ControllerButton.B], result.Configuration.Shortcut.Buttons);
        Assert.Contains(result.Warnings, warning => warning.Contains("leak gameplay input", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Normalize_skips_duplicate_profile_ids()
    {
        var result = ConfigurationValidator.Normalize(new AppConfiguration
        {
            Profiles =
            [
                MappingProfile.Default,
                new MappingProfile { Id = "Elden Ring", Name = "Elden Ring" },
                new MappingProfile { Id = "elden-ring", Name = "Duplicate" },
            ],
        });

        Assert.Equal(2, result.Configuration.Profiles.Count);
        Assert.Equal("elden-ring", result.Configuration.Profiles[1].Id);
        Assert.Contains(result.Warnings, warning => warning.Contains("duplicated", StringComparison.OrdinalIgnoreCase));
    }
}
