namespace AllyBindings.Core;

public enum BackendHealth
{
    Unavailable,
    Preview,
    Partial,
    Ready,
    Degraded,
}

public sealed record BackendStatus(
    string Name,
    BackendHealth Health,
    bool CanRemap,
    bool PhysicalPassthroughIntact,
    string Message);

public sealed record BackendApplyResult(bool CommandAccepted, string Message, BackendStatus Status);

public interface IControllerBackend : IAsyncDisposable
{
    BackendStatus GetStatus();
    Task<BackendStatus> InitializeAsync(CancellationToken cancellationToken = default);
    Task<BackendApplyResult> ApplyAsync(MappingProfile profile, CancellationToken cancellationToken = default);
    Task<BackendApplyResult> RestoreDefaultAsync(CancellationToken cancellationToken = default);
}

public sealed class PreviewControllerBackend : IControllerBackend
{
    private string _selectedProfile = MappingProfile.Default.Name;

    public BackendStatus GetStatus() => new(
        "Preview",
        BackendHealth.Preview,
        CanRemap: false,
        PhysicalPassthroughIntact: true,
        $"{_selectedProfile} is selected in preview mode; physical remapping is not enabled.");

    public Task<BackendStatus> InitializeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(GetStatus());

    public Task<BackendApplyResult> ApplyAsync(MappingProfile profile, CancellationToken cancellationToken = default)
    {
        _selectedProfile = profile.Name;
        var status = GetStatus();
        return Task.FromResult(new BackendApplyResult(false, status.Message, status));
    }

    public Task<BackendApplyResult> RestoreDefaultAsync(CancellationToken cancellationToken = default)
    {
        _selectedProfile = MappingProfile.Default.Name;
        var status = GetStatus();
        return Task.FromResult(new BackendApplyResult(false, status.Message, status));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Applies only the two ASUS firmware-managed rear paddles. Standard XInput
/// remapping deliberately remains preview-only until the physical-hide and
/// virtual-output hardware spike passes.
/// </summary>
public sealed class AsusRearButtonControllerBackend(IAsusRearButtonDevice device) : IControllerBackend
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private AsusRearButtonDeviceStatus _deviceStatus = new(
        false,
        false,
        "Unknown",
        [],
        "ASUS rear-button hardware has not been probed yet.");
    private string _selectedProfile = MappingProfile.Default.Name;

    public BackendStatus GetStatus()
    {
        if (!_deviceStatus.IsSupportedModel)
        {
            return new(
                "ASUS M1/M2 + Preview",
                BackendHealth.Unavailable,
                CanRemap: false,
                PhysicalPassthroughIntact: true,
                $"{_deviceStatus.Message} Standard mappings remain preview-only.");
        }

        if (!_deviceStatus.IsAvailable)
        {
            return new(
                "ASUS M1/M2 + Preview",
                BackendHealth.Degraded,
                CanRemap: false,
                PhysicalPassthroughIntact: true,
                $"{_deviceStatus.Message} No controller settings were changed.");
        }

        return new(
            "ASUS M1/M2 + Preview",
            BackendHealth.Partial,
            CanRemap: true,
            PhysicalPassthroughIntact: true,
            $"M1/M2 writes are available for {_deviceStatus.Model}; standard mappings remain preview-only. Last accepted command: {_selectedProfile}. Live Armoury state is not readable.");
    }

    public async Task<BackendStatus> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _deviceStatus = await device.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return GetStatus();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task<BackendApplyResult> ApplyAsync(
        MappingProfile profile,
        CancellationToken cancellationToken = default)
    {
        var m1 = profile.Bindings.GetValueOrDefault(ControllerButton.M1, ControllerButton.M1);
        var m2 = profile.Bindings.GetValueOrDefault(ControllerButton.M2, ControllerButton.M2);
        return ApplyReportAsync(
            profile.Name,
            AsusRearButtonProtocol.BuildMappingReport(m1, m2),
            allowReprobe: false,
            cancellationToken);
    }

    public Task<BackendApplyResult> RestoreDefaultAsync(CancellationToken cancellationToken = default) =>
        ApplyReportAsync(
            MappingProfile.Default.Name,
            AsusRearButtonProtocol.BuildNativeResetReport(),
            allowReprobe: true,
            cancellationToken);

    private async Task<BackendApplyResult> ApplyReportAsync(
        string profileName,
        byte[] report,
        bool allowReprobe,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = GetStatus();
            if (!status.CanRemap && allowReprobe)
            {
                _deviceStatus = await device.InitializeAsync(cancellationToken).ConfigureAwait(false);
                status = GetStatus();
            }
            if (!status.CanRemap)
            {
                return new(false, status.Message, status);
            }

            var write = await device.WriteFeatureReportAsync(report, cancellationToken).ConfigureAwait(false);
            _deviceStatus = device.GetStatus();
            if (write.Succeeded == 0)
            {
                status = GetStatus() with
                {
                    Health = BackendHealth.Degraded,
                    CanRemap = false,
                    Message = $"M1/M2 write failed safely: {write.Message}",
                };
                return new(false, status.Message, status);
            }

            _selectedProfile = profileName;
            status = GetStatus() with
            {
                Message = $"M1/M2 command accepted for {profileName}; live state cannot be read back and Armoury may overwrite it. Standard mappings remain preview-only. {write.Message}",
            };
            return new(true, status.Message, status);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await device.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }
}
