using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class AsusRearButtonProtocolTests
{
    [Fact]
    public void Capture_release_keeps_custom_and_recovery_writes_locked()
    {
        Assert.False(ArmouryProtocolValidation.CustomWritesApproved);
        Assert.False(ArmouryProtocolValidation.RecoveryWritesApproved);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void Write_authorization_uses_the_gate_for_the_requested_operation(
        bool isRecoveryReset,
        bool customWritesApproved,
        bool recoveryWritesApproved,
        bool expected)
    {
        Assert.Equal(
            expected,
            ArmouryProtocolValidation.IsOperationApproved(
                isRecoveryReset,
                customWritesApproved,
                recoveryWritesApproved));
    }

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

    [Fact]
    public void Wire_report_comparison_accepts_only_the_expected_packet_plus_zero_padding()
    {
        var expected = AsusRearButtonProtocol.BuildMappingReport(ControllerButton.A, ControllerButton.B);
        var padded = new byte[64];
        expected.CopyTo(padded, 0);

        Assert.True(AsusRearButtonProtocol.MatchesWireReport(expected, expected));
        Assert.True(AsusRearButtonProtocol.MatchesWireReport(padded, expected));
        Assert.False(AsusRearButtonProtocol.MatchesWireReport(expected[..^1], expected));

        padded[^1] = 0x01;
        Assert.False(AsusRearButtonProtocol.MatchesWireReport(padded, expected));
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
    public void Public_backend_constructor_has_no_write_approval_override()
    {
        var constructor = Assert.Single(typeof(AsusRearButtonControllerBackend).GetConstructors());
        var parameter = Assert.Single(constructor.GetParameters());
        Assert.Equal(typeof(IAsusRearButtonDevice), parameter.ParameterType);
    }

    [Fact]
    public async Task Locked_custom_apply_cannot_probe_or_write()
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
    public async Task Initialized_backend_rejects_custom_and_reset_writes()
    {
        var device = FakeRearButtonDevice.Available();
        await using var backend = new AsusRearButtonControllerBackend(device);
        var initialized = await backend.InitializeAsync();

        var custom = await backend.ApplyAsync(new MappingProfile
        {
            Id = "rear",
            Name = "Rear",
            Bindings = new Dictionary<ControllerButton, ControllerButton>
            {
                [ControllerButton.M1] = ControllerButton.A,
            },
        });
        var reset = await backend.RestoreDefaultAsync();

        Assert.Equal(BackendHealth.Preview, initialized.Health);
        Assert.False(initialized.CanRemap);
        Assert.False(custom.CommandAccepted);
        Assert.False(reset.CommandAccepted);
        Assert.Empty(device.Reports);
        Assert.Contains("locked", custom.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capture_release_cannot_reprobe_or_send_a_recovery_reset()
    {
        var device = FakeRearButtonDevice.Available();
        await using var backend = new AsusRearButtonControllerBackend(device);

        var reset = await backend.RestoreDefaultAsync();

        Assert.False(reset.CommandAccepted);
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

        public Task<AsusRearButtonReadResult> ReadFeatureReportAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AsusRearButtonReadResult(
                Attempted: status.IsAvailable,
                Succeeded: false,
                Reads: [],
                Message: "No fake read configured."));

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
