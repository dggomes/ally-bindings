using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AllyBindings.SoftwareProbe;

namespace AllyBindings.Core.Tests;

public sealed class SoftwareProbeEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ally-bindings-probe-tests", Guid.NewGuid().ToString("N"));

    private static SoftwareProbeCapabilities Capabilities => new(
        "0.3.0-preview.21",
        "Windows 11",
        "RC73XA",
        [0, 2],
        ViGEmBusInstalled: true,
        HidHideInstalled: false,
        ViGEmBusStatus: "Running",
        HidHideStatus: "Not installed");

    [Fact]
    public void Session_retains_only_f11_f12_events_and_declares_software_only_safety()
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
        var session = SoftwareProbeSession.Create(Capabilities, now);

        session = session.AddKeyEvent(new(now.AddSeconds(1), "F12", true, false, true, "capture-suppress"));
        session = session.AddKeyEvent(new(now.AddSeconds(2), "F11", false, true, false, "virtual-bridge"));

        Assert.Equal(2, session.KeyEvents.Length);
        Assert.Contains("no ASUS HID writes", session.SafetyMode, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.AddKeyEvent(new(now.AddSeconds(3), "A", true, false, false, "capture-observe")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.AddKeyEvent(new(now.AddSeconds(3), "F11", true, false, false, "broad-keylogger")));
    }

    [Fact]
    public void Checkpoints_are_allowlisted_and_replaced_by_name()
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
        var session = SoftwareProbeSession.Create(Capabilities, now)
            .SetCheckpoint(SoftwareProbeCheckpoints.KeyboardCapture, SoftwareProbeCheckpointResult.Fail, now.AddMinutes(1))
            .SetCheckpoint(SoftwareProbeCheckpoints.KeyboardCapture, SoftwareProbeCheckpointResult.Pass, now.AddMinutes(2));

        var checkpoint = Assert.Single(session.Checkpoints);
        Assert.Equal(SoftwareProbeCheckpointResult.Pass, checkpoint.Result);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.SetCheckpoint("arbitrary", SoftwareProbeCheckpointResult.Pass, now));
    }

    [Fact]
    public void Completed_session_rejects_late_events_and_checkpoints()
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
        var session = ReadyToFinalize(now).FinalizeSession(now.AddMinutes(10));

        Assert.Throws<InvalidOperationException>(() =>
            session.AddKeyEvent(new(now.AddMinutes(11), "F11", true, false, true, "capture-observe")));
        Assert.Throws<InvalidOperationException>(() =>
            session.SetCheckpoint(SoftwareProbeCheckpoints.ArmouryRestored, SoftwareProbeCheckpointResult.Pass, now.AddMinutes(11)));
    }

    [Fact]
    public void Evidence_store_round_trips_and_builds_a_minimal_hashed_zip()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var session = ReadyToFinalize(now)
            .AddKeyEvent(new(now.AddSeconds(1), "F12", true, false, true, "capture-observe"));
        var directory = SoftwareProbeEvidenceStore.CreateSessionDirectory(_root, session);

        var roundTrip = SoftwareProbeEvidenceStore.Load(directory);
        Assert.Equal(session.SchemaVersion, roundTrip.SchemaVersion);
        Assert.Equal(session.SessionId, roundTrip.SessionId);
        Assert.Equal(session.CreatedAtUtc, roundTrip.CreatedAtUtc);
        Assert.Equal(session.UpdatedAtUtc, roundTrip.UpdatedAtUtc);
        Assert.Equal(session.SafetyMode, roundTrip.SafetyMode);
        Assert.Equal(session.Capabilities.ConnectedXInputSlots.ToArray(), roundTrip.Capabilities.ConnectedXInputSlots.ToArray());
        Assert.Equal(session.KeyEvents.ToArray(), roundTrip.KeyEvents.ToArray());
        Assert.Equal(session.Checkpoints.ToArray(), roundTrip.Checkpoints.ToArray());

        var result = SoftwareProbeEvidenceStore.FinalizeBundle(directory, Path.Combine(_root, "bundle.zip"));
        Assert.True(result.Complete);
        Assert.Equal(64, result.Sha256.Length);
        Assert.True(File.Exists(result.ZipPath));

        using var archive = ZipFile.OpenRead(result.ZipPath);
        Assert.Equal(3, archive.Entries.Count);
        Assert.Contains(archive.Entries, entry => entry.FullName == "README.txt");
        Assert.Contains(archive.Entries, entry => entry.FullName == "manifest.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "session.json");

        var manifestEntry = archive.GetEntry("manifest.json")!;
        using (var manifestStream = manifestEntry.Open())
        using (var manifest = JsonDocument.Parse(manifestStream))
        {
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                var entry = archive.GetEntry(file.GetProperty("name").GetString()!)!;
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                var bytes = memory.ToArray();
                Assert.Equal(file.GetProperty("bytes").GetInt32(), bytes.Length);
                Assert.Equal(file.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            }
        }

        var sessionEntry = archive.GetEntry("session.json")!;
        using var sessionStream = sessionEntry.Open();
        using var document = JsonDocument.Parse(sessionStream);
        var json = document.RootElement.GetRawText();
        Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VID_", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devicePath", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(999)]
    public void Evidence_store_rejects_incompatible_schema(int schemaVersion)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "session.json"), $"{{\"schemaVersion\":{schemaVersion}}}");
        Assert.Throws<InvalidDataException>(() => SoftwareProbeEvidenceStore.Load(_root));
    }

    [Fact]
    public void Finalization_requires_every_decision_and_a_passed_restoration()
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
        var empty = SoftwareProbeSession.Create(Capabilities, now);
        Assert.Throws<InvalidOperationException>(() => empty.FinalizeSession(now.AddMinutes(1)));

        var notRestored = ReadyToFinalize(now)
            .SetCheckpoint(SoftwareProbeCheckpoints.ArmouryRestored, SoftwareProbeCheckpointResult.Fail, now.AddMinutes(9));
        Assert.Throws<InvalidOperationException>(() => notRestored.FinalizeSession(now.AddMinutes(10)));
    }

    [Fact]
    public void Evidence_store_rejects_tampered_broad_key_events()
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
        var tampered = SoftwareProbeSession.Create(Capabilities, now) with
        {
            UpdatedAtUtc = now.AddSeconds(1),
            KeyEvents = [new(now.AddSeconds(1), "A", true, false, false, "capture-observe")],
        };
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(
            Path.Combine(_root, "session.json"),
            JsonSerializer.SerializeToUtf8Bytes(tampered, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        Assert.Throws<InvalidDataException>(() => SoftwareProbeEvidenceStore.Load(_root));
    }

    [Fact]
    public void Load_rejects_tampered_free_form_notes()
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
        var tampered = SoftwareProbeSession.Create(Capabilities, now) with
        {
            UpdatedAtUtc = now.AddSeconds(1),
            Checkpoints = [new(
                SoftwareProbeCheckpoints.KeyboardCapture,
                SoftwareProbeCheckpointResult.Pass,
                now.AddSeconds(1),
                "secret path")],
        };
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(
            Path.Combine(_root, "session.json"),
            JsonSerializer.SerializeToUtf8Bytes(tampered, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        Assert.Throws<InvalidDataException>(() => SoftwareProbeEvidenceStore.Load(_root));
    }

    [Fact]
    public void Concurrent_updates_merge_without_losing_key_events()
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
        var session = SoftwareProbeSession.Create(Capabilities, now);
        var directory = SoftwareProbeEvidenceStore.CreateSessionDirectory(_root, session);

        Parallel.For(0, 64, index => SoftwareProbeEvidenceStore.Update(directory, current => current.AddKeyEvent(new(
            now.AddTicks(index + 1),
            index % 2 == 0 ? "F11" : "F12",
            IsKeyDown: index % 2 == 0,
            IsInjected: false,
            WasSuppressed: true,
            Mode: "capture-suppress"))));

        Assert.Equal(64, SoftwareProbeEvidenceStore.Load(directory).KeyEvents.Length);
    }

    [Fact]
    public void Finalize_refuses_an_active_capture_and_source_collisions()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-20);
        var session = ReadyToFinalize(now);
        var directory = SoftwareProbeEvidenceStore.CreateSessionDirectory(_root, session);

        using (SoftwareProbeEvidenceStore.AcquireCaptureLease(directory))
        {
            Assert.Throws<IOException>(() => SoftwareProbeEvidenceStore.FinalizeBundle(directory, Path.Combine(_root, "busy.zip")));
        }
        Assert.Throws<ArgumentException>(() => SoftwareProbeEvidenceStore.FinalizeBundle(directory, Path.Combine(directory, "collision.zip")));
        Assert.False(SoftwareProbeEvidenceStore.Load(directory).Complete);
    }

    private static SoftwareProbeSession ReadyToFinalize(DateTimeOffset now)
    {
        var session = SoftwareProbeSession.Create(Capabilities, now);
        var index = 1;
        foreach (var name in SoftwareProbeCheckpoints.Allowed.OrderBy(name => name, StringComparer.Ordinal))
        {
            var result = name is SoftwareProbeCheckpoints.ArmouryBaselineSaved or SoftwareProbeCheckpoints.ArmouryRestored
                ? SoftwareProbeCheckpointResult.Pass
                : SoftwareProbeCheckpointResult.Skipped;
            session = session.SetCheckpoint(name, result, now.AddMinutes(index++));
        }
        return session;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
