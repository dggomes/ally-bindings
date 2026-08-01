using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class AsusRearButtonProtocolTests
{
    [Fact]
    public void Native_reset_report_writes_both_corroborated_modifier_actions()
    {
        var report = AsusRearButtonProtocol.BuildNativeResetReport();

        Assert.Equal(AsusRearButtonProtocol.ReportLength, report.Length);
        Assert.Equal(new byte[] { 0x5A, 0xD1, 0x02, 0x08, 0x2C }, report[..5]);
        AssertAction(report, 5, 0x02, 0x00, 0x8E);
        AssertAction(report, 16, 0x02, 0x00, 0x8E);
        AssertAction(report, 27, 0x02, 0x00, 0x8F);
        AssertAction(report, 38, 0x02, 0x00, 0x8F);
    }

    [Fact]
    public void Custom_report_maps_each_rear_button_in_primary_and_secondary_slots()
    {
        var report = AsusRearButtonProtocol.BuildMappingReport(
            ControllerButton.RightTrigger,
            ControllerButton.A);

        AssertAction(report, 5, 0x01, 0x01, 0x00);
        AssertAction(report, 16, 0x01, 0x01, 0x00);
        AssertAction(report, 27, 0x01, 0x0E, 0x00);
        AssertAction(report, 38, 0x01, 0x0E, 0x00);
    }

    [Theory]
    [InlineData(ControllerButton.None)]
    [InlineData(ControllerButton.M2)]
    public void Invalid_cross_or_empty_m1_targets_are_rejected(ControllerButton target)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AsusRearButtonProtocol.BuildMappingReport(target, ControllerButton.M2));
    }

    private static void AssertAction(byte[] report, int offset, byte kind, byte code1, byte code2)
    {
        Assert.Equal(kind, report[offset]);
        Assert.Equal(code1, report[offset + 1]);
        Assert.Equal(code2, report[offset + 2]);
    }
}

public sealed class AsusRearButtonControllerBackendTests
{
    [Fact]
    public async Task Applies_profile_rear_bindings_and_reports_partial_backend()
    {
        var device = FakeRearButtonDevice.Available();
        await using var backend = new AsusRearButtonControllerBackend(device);
        var initialized = await backend.InitializeAsync();
        var profile = new MappingProfile
        {
            Id = "rear",
            Name = "Rear",
            Bindings = new Dictionary<ControllerButton, ControllerButton>
            {
                [ControllerButton.M1] = ControllerButton.B,
                [ControllerButton.M2] = ControllerButton.LeftTrigger,
            },
        };

        var result = await backend.ApplyAsync(profile);

        Assert.Equal(BackendHealth.Partial, initialized.Health);
        Assert.True(result.CommandAccepted);
        Assert.Single(device.Reports);
        AssertAction(device.Reports[0], 5, 0x01, 0x0D);
        AssertAction(device.Reports[0], 27, 0x01, 0x02);
        Assert.Contains("command accepted", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("live state cannot be read back", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("standard mappings remain preview-only", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Custom_apply_cannot_reprobe_and_write_behind_the_recovery_marker()
    {
        var device = FakeRearButtonDevice.Available();
        await using var backend = new AsusRearButtonControllerBackend(device);
        var profile = new MappingProfile
        {
            Id = "rear",
            Name = "Rear",
            Bindings = new Dictionary<ControllerButton, ControllerButton>
            {
                [ControllerButton.M1] = ControllerButton.A,
            },
        };

        var result = await backend.ApplyAsync(profile);

        Assert.False(result.CommandAccepted);
        Assert.Equal(0, device.InitializeCalls);
        Assert.Empty(device.Reports);
    }

    [Fact]
    public async Task Unsupported_machine_never_attempts_a_write()
    {
        var device = FakeRearButtonDevice.Unsupported();
        await using var backend = new AsusRearButtonControllerBackend(device);
        await backend.InitializeAsync();

        var result = await backend.ApplyAsync(MappingProfile.Default);

        Assert.False(result.CommandAccepted);
        Assert.Empty(device.Reports);
        Assert.Equal(BackendHealth.Unavailable, result.Status.Health);
    }

    [Fact]
    public async Task Restore_reprobes_and_writes_native_reset_report()
    {
        var device = FakeRearButtonDevice.Available();
        await using var backend = new AsusRearButtonControllerBackend(device);

        var result = await backend.RestoreDefaultAsync();

        Assert.True(result.CommandAccepted);
        Assert.Equal(1, device.InitializeCalls);
        Assert.Equal(AsusRearButtonProtocol.BuildNativeResetReport(), Assert.Single(device.Reports));
    }

    private static void AssertAction(byte[] report, int offset, byte kind, byte code)
    {
        Assert.Equal(kind, report[offset]);
        Assert.Equal(code, report[offset + 1]);
    }

    private sealed class FakeRearButtonDevice(AsusRearButtonDeviceStatus status) : IAsusRearButtonDevice
    {
        public List<byte[]> Reports { get; } = [];
        public int InitializeCalls { get; private set; }

        public static FakeRearButtonDevice Available() => new(new(
            true,
            true,
            "RC73XA",
            ["VID_0B05&PID_1B6E:report_5A"],
            "Ready"));

        public static FakeRearButtonDevice Unsupported() => new(new(
            false,
            false,
            "OTHER",
            [],
            "Unsupported"));

        public Task<AsusRearButtonDeviceStatus> InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCalls++;
            return Task.FromResult(status);
        }

        public AsusRearButtonDeviceStatus GetStatus() => status;

        public Task<AsusRearButtonWriteResult> WriteFeatureReportAsync(
            byte[] report,
            CancellationToken cancellationToken = default)
        {
            Reports.Add([.. report]);
            return Task.FromResult(new AsusRearButtonWriteResult(1, 1, "Written"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
