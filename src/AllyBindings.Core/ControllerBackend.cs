namespace AllyBindings.Core;

public enum BackendHealth
{
    Unavailable,
    Preview,
    Ready,
    Degraded,
}

public sealed record BackendStatus(
    string Name,
    BackendHealth Health,
    bool CanRemap,
    bool PhysicalPassthroughIntact,
    string Message);

public sealed record BackendApplyResult(bool AppliedToController, string Message, BackendStatus Status);

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
