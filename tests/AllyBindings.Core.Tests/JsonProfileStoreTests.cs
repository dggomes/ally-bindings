using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class JsonProfileStoreTests : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ally-bindings-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Save_and_load_round_trip_with_backup()
    {
        var path = System.IO.Path.Combine(_directory, "config.json");
        var store = new JsonProfileStore(path);
        var initial = AppConfiguration.CreateDefault();
        await store.SaveAsync(initial);
        var changed = initial with
        {
            ActiveProfileId = "elden-ring",
            Profiles =
            [
                MappingProfile.Default,
                new MappingProfile
                {
                    Id = "elden-ring",
                    Name = "Elden Ring",
                    Bindings = new Dictionary<ControllerButton, ControllerButton>
                    {
                        [ControllerButton.A] = ControllerButton.B,
                    },
                },
            ],
        };

        await store.SaveAsync(changed);
        var loaded = await store.LoadAsync();

        Assert.Equal("elden-ring", loaded.Configuration.ActiveProfileId);
        Assert.Equal(2, loaded.Configuration.Profiles.Count);
        Assert.True(File.Exists($"{path}.bak"));
    }

    [Fact]
    public async Task Concurrent_saves_remain_valid_and_leave_no_temp_files()
    {
        var path = System.IO.Path.Combine(_directory, "config.json");
        var store = new JsonProfileStore(path);
        var writes = Enumerable.Range(1, 12).Select(index => store.SaveAsync(new AppConfiguration
        {
            Profiles =
            [
                MappingProfile.Default,
                new MappingProfile { Id = $"profile-{index}", Name = $"Profile {index}" },
            ],
        }));

        await Task.WhenAll(writes);
        var loaded = await store.LoadAsync();

        Assert.Equal(2, loaded.Configuration.Profiles.Count);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Null_and_unsupported_json_values_are_normalized_safely()
    {
        Directory.CreateDirectory(_directory);
        var path = System.IO.Path.Combine(_directory, "config.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "activeProfileId": null,
              "controllerIndex": 99,
              "shortcut": null,
              "profiles": [
                null,
                { "id": null, "name": null, "enabled": true, "bindings": null }
              ]
            }
            """);
        var store = new JsonProfileStore(path);

        var loaded = await store.LoadAsync();

        Assert.Equal("default", loaded.Configuration.ActiveProfileId);
        Assert.Null(loaded.Configuration.ControllerIndex);
        Assert.Single(loaded.Configuration.Profiles);
        Assert.Equal([ControllerButton.View, ControllerButton.Menu], loaded.Configuration.Shortcut.Buttons);
        Assert.NotEmpty(loaded.Warnings);
    }

    [Fact]
    public async Task Invalid_primary_restores_last_valid_backup()
    {
        var path = System.IO.Path.Combine(_directory, "config.json");
        var store = new JsonProfileStore(path);
        var first = AppConfiguration.CreateDefault() with
        {
            Profiles = [MappingProfile.Default, new MappingProfile { Id = "safe", Name = "Safe" }],
            ActiveProfileId = "safe",
        };
        await store.SaveAsync(first);
        await store.SaveAsync(first with { ActiveProfileId = MappingProfile.Default.Id });
        await File.WriteAllTextAsync(path, "{ broken");

        var loaded = await store.LoadAsync();

        Assert.Equal("safe", loaded.Configuration.ActiveProfileId);
        Assert.Contains(loaded.Warnings, warning => warning.Contains("backup", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"activeProfileId\": \"safe\"", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Future_schema_is_not_overwritten()
    {
        Directory.CreateDirectory(_directory);
        var path = System.IO.Path.Combine(_directory, "config.json");
        var futureJson = """
            {
              "schemaVersion": 99,
              "futureField": "must survive"
            }
            """;
        await File.WriteAllTextAsync(path, futureJson);
        var store = new JsonProfileStore(path);

        await Assert.ThrowsAsync<UnsupportedConfigurationVersionException>(() => store.LoadAsync());

        Assert.Equal(futureJson, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Load_recovers_invalid_json_without_losing_evidence()
    {
        Directory.CreateDirectory(_directory);
        var path = System.IO.Path.Combine(_directory, "config.json");
        await File.WriteAllTextAsync(path, "{ broken json");
        var store = new JsonProfileStore(path);

        var loaded = await store.LoadAsync();

        Assert.Equal("default", loaded.Configuration.ActiveProfileId);
        Assert.NotEmpty(loaded.Warnings);
        Assert.Single(Directory.GetFiles(_directory, "config.json.corrupt-*"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
