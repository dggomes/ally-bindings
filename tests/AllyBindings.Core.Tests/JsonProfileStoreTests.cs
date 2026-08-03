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
            ArmouryTapTeardownBlockedSinceUtc = DateTimeOffset.Parse("2026-08-03T01:00:00Z"),
            ArmouryTapTeardownBootIdentifier = Guid.Parse("c2765510-09cc-43c8-a7d0-ac0df1b490fe"),
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
        Assert.Equal(changed.ArmouryTapTeardownBlockedSinceUtc, loaded.Configuration.ArmouryTapTeardownBlockedSinceUtc);
        Assert.Equal(changed.ArmouryTapTeardownBootIdentifier, loaded.Configuration.ArmouryTapTeardownBootIdentifier);
        Assert.Equal(2, loaded.Configuration.Profiles.Count);
        Assert.True(File.Exists($"{path}.bak"));
    }

    [Fact]
    public async Task Trigger_source_mappings_round_trip_under_schema_three()
    {
        var path = System.IO.Path.Combine(_directory, "config.json");
        var store = new JsonProfileStore(path);
        var configuration = AppConfiguration.CreateDefault() with
        {
            Profiles =
            [
                MappingProfile.Default,
                new MappingProfile
                {
                    Id = "trigger-swap",
                    Name = "Trigger swap",
                    Bindings = new Dictionary<ControllerButton, ControllerButton>
                    {
                        [ControllerButton.LeftTrigger] = ControllerButton.RightTrigger,
                        [ControllerButton.RightTrigger] = ControllerButton.LeftTrigger,
                    },
                },
            ],
        };

        await store.SaveAsync(configuration);
        var loaded = await store.LoadAsync();
        var profile = Assert.Single(loaded.Configuration.Profiles, candidate => candidate.Id == "trigger-swap");

        Assert.Equal(3, loaded.Configuration.SchemaVersion);
        Assert.Equal(ControllerButton.RightTrigger, profile.Bindings[ControllerButton.LeftTrigger]);
        Assert.Equal(ControllerButton.LeftTrigger, profile.Bindings[ControllerButton.RightTrigger]);
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
    public async Task Armed_tap_barrier_survives_concurrent_stale_save_and_recovery_backup()
    {
        var path = System.IO.Path.Combine(_directory, "config.json");
        var store = new JsonProfileStore(path);
        var baseline = AppConfiguration.CreateDefault();
        await store.SaveAsync(baseline);
        var blockedSince = DateTimeOffset.Parse("2026-08-03T12:00:00Z");
        var bootIdentifier = Guid.Parse("0df333b0-7122-4374-ac00-081f66cf68d7");
        var armed = baseline with
        {
            ArmouryTapTeardownBlockedSinceUtc = blockedSince,
            ArmouryTapTeardownBootIdentifier = bootIdentifier,
        };

        await Task.WhenAll(
            store.ArmTapTeardownBarrierAsync(armed),
            store.SaveAsync(baseline with { LastUpdateCheckUtc = blockedSince.AddMinutes(1) }));

        await File.WriteAllTextAsync(path, "{ broken");
        var recovered = await new JsonProfileStore(path).LoadAsync();
        Assert.Equal(blockedSince, recovered.Configuration.ArmouryTapTeardownBlockedSinceUtc);
        Assert.Equal(bootIdentifier, recovered.Configuration.ArmouryTapTeardownBootIdentifier);
    }

    [Fact]
    public async Task Ordinary_save_cannot_clear_armed_tap_barrier_but_explicit_clear_updates_primary_and_backup()
    {
        var path = System.IO.Path.Combine(_directory, "config.json");
        var store = new JsonProfileStore(path);
        var baseline = AppConfiguration.CreateDefault();
        await store.SaveAsync(baseline);
        var blockedSince = DateTimeOffset.Parse("2026-08-03T12:00:00Z");
        var armed = baseline with
        {
            ArmouryTapTeardownBlockedSinceUtc = blockedSince,
            ArmouryTapTeardownBootIdentifier = Guid.Parse("70c4a72b-b332-4624-b6e5-22d6c66d2f0f"),
        };
        await store.ArmTapTeardownBarrierAsync(armed);
        Assert.True(File.Exists($"{path}.tap-barrier"));

        await store.SaveAsync(baseline);
        Assert.Equal(blockedSince, (await store.LoadAsync()).Configuration.ArmouryTapTeardownBlockedSinceUtc);

        await store.ClearTapTeardownBarrierAsync(baseline);
        Assert.False(File.Exists($"{path}.tap-barrier"));
        Assert.Null((await store.LoadAsync()).Configuration.ArmouryTapTeardownBlockedSinceUtc);
        await File.WriteAllTextAsync(path, "{ broken");
        Assert.NotNull((await new JsonProfileStore(path).LoadAsync()).Configuration.ArmouryTapTeardownBlockedSinceUtc);
    }

    [Fact]
    public async Task Legacy_barrier_boot_baseline_is_persisted_to_config_copies_and_sentinel()
    {
        var path = System.IO.Path.Combine(_directory, "config.json");
        var blockedSince = DateTimeOffset.Parse("2026-08-03T12:00:00Z");
        var legacy = AppConfiguration.CreateDefault() with
        {
            ArmouryTapTeardownBlockedSinceUtc = blockedSince,
            ArmouryTapTeardownBootIdentifier = null,
        };
        await new JsonProfileStore(path).SaveAsync(legacy);
        var store = new JsonProfileStore(path);
        var loaded = await store.LoadAsync();
        var bootIdentifier = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await store.EstablishTapTeardownBootBaselineAsync(loaded.Configuration with
        {
            ArmouryTapTeardownBootIdentifier = bootIdentifier,
        });

        var restarted = await new JsonProfileStore(path).LoadAsync();
        Assert.Equal(blockedSince, restarted.Configuration.ArmouryTapTeardownBlockedSinceUtc);
        Assert.Equal(bootIdentifier, restarted.Configuration.ArmouryTapTeardownBootIdentifier);
        Assert.Contains(bootIdentifier.ToString(), await File.ReadAllTextAsync($"{path}.bak"));
        Assert.Contains(bootIdentifier.ToString(), await File.ReadAllTextAsync($"{path}.tap-barrier"));
    }

    [Fact]
    public async Task Barrier_sentinel_fails_closed_until_explicit_clear_commit()
    {
        var path = System.IO.Path.Combine(_directory, "config.json");
        var store = new JsonProfileStore(path);
        var blockedSince = DateTimeOffset.Parse("2026-08-03T12:00:00Z");
        var armed = AppConfiguration.CreateDefault() with
        {
            ArmouryTapTeardownBlockedSinceUtc = blockedSince,
            ArmouryTapTeardownBootIdentifier = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        };
        await store.ArmTapTeardownBarrierAsync(armed);

        // Model a crash after both cleared copies land but before sentinel
        // deletion commits the release.
        var clearedPath = System.IO.Path.Combine(_directory, "cleared.json");
        await new JsonProfileStore(clearedPath).SaveAsync(AppConfiguration.CreateDefault());
        File.Copy(clearedPath, path, overwrite: true);
        File.Copy(clearedPath, $"{path}.bak", overwrite: true);

        var recovered = await new JsonProfileStore(path).LoadAsync();
        Assert.Equal(blockedSince, recovered.Configuration.ArmouryTapTeardownBlockedSinceUtc);
        Assert.Equal(armed.ArmouryTapTeardownBootIdentifier, recovered.Configuration.ArmouryTapTeardownBootIdentifier);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Legacy_single_copy_barrier_cannot_be_lost_to_stale_backup_recovery(bool deletePrimary)
    {
        var path = System.IO.Path.Combine(_directory, $"legacy-{deletePrimary}.json");
        var store = new JsonProfileStore(path);
        var baseline = AppConfiguration.CreateDefault();
        await store.SaveAsync(baseline);

        // Preview.18 used one ordinary save: the primary became armed while
        // the backup retained the preceding unarmed configuration.
        await store.SaveAsync(baseline with
        {
            ArmouryTapTeardownBlockedSinceUtc = DateTimeOffset.Parse("2026-08-03T12:00:00Z"),
            ArmouryTapTeardownBootIdentifier = null,
        });
        if (deletePrimary)
            File.Delete(path);
        else
            await File.WriteAllTextAsync(path, "{ broken");

        var recovered = await new JsonProfileStore(path).LoadAsync();

        Assert.NotNull(recovered.Configuration.ArmouryTapTeardownBlockedSinceUtc);
        Assert.Null(recovered.Configuration.ArmouryTapTeardownBootIdentifier);
        Assert.True(File.Exists($"{path}.tap-barrier"));
        Assert.Contains(recovered.Warnings, warning => warning.Contains("single-copy barrier", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Missing_primary_restores_armed_recovery_backup()
    {
        var path = System.IO.Path.Combine(_directory, "config.json");
        var store = new JsonProfileStore(path);
        var blockedSince = DateTimeOffset.Parse("2026-08-03T12:00:00Z");
        var bootIdentifier = Guid.Parse("58efadba-0163-49ef-b1eb-7eeb5ff42f09");
        var armed = AppConfiguration.CreateDefault() with
        {
            ArmouryTapTeardownBlockedSinceUtc = blockedSince,
            ArmouryTapTeardownBootIdentifier = bootIdentifier,
        };
        await store.ArmTapTeardownBarrierAsync(armed);
        File.Delete(path);

        var recovered = await new JsonProfileStore(path).LoadAsync();

        Assert.Equal(blockedSince, recovered.Configuration.ArmouryTapTeardownBlockedSinceUtc);
        Assert.Equal(bootIdentifier, recovered.Configuration.ArmouryTapTeardownBootIdentifier);
        Assert.Contains(recovered.Warnings, warning => warning.Contains("missing", StringComparison.OrdinalIgnoreCase));
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
        Assert.NotNull(loaded.Configuration.ArmouryTapTeardownBlockedSinceUtc);
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
