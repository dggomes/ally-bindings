using AllyBindings.Core;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace AllyBindings.Windows;

public sealed record VirtualControllerRuntimeState(
    int PhysicalXInputIndex,
    ControllerSnapshot PhysicalSnapshot,
    ControllerSnapshot OutputSnapshot,
    bool M1Down,
    bool M2Down,
    bool IsOutputConnected,
    string? Fault);

public sealed class VirtualControllerBackend : IControllerBackend
{
    private static readonly (ControllerButton Source, Xbox360Button Target)[] ButtonMap =
    [
        (ControllerButton.DPadUp, Xbox360Button.Up), (ControllerButton.DPadDown, Xbox360Button.Down),
        (ControllerButton.DPadLeft, Xbox360Button.Left), (ControllerButton.DPadRight, Xbox360Button.Right),
        (ControllerButton.Menu, Xbox360Button.Start), (ControllerButton.View, Xbox360Button.Back),
        (ControllerButton.LeftStick, Xbox360Button.LeftThumb), (ControllerButton.RightStick, Xbox360Button.RightThumb),
        (ControllerButton.LeftBumper, Xbox360Button.LeftShoulder), (ControllerButton.RightBumper, Xbox360Button.RightShoulder),
        (ControllerButton.A, Xbox360Button.A), (ControllerButton.B, Xbox360Button.B),
        (ControllerButton.X, Xbox360Button.X), (ControllerButton.Y, Xbox360Button.Y),
    ];

    private readonly object _gate = new();
    private readonly RealtimeControllerPipeline _pipeline = new();
    private readonly int _physicalXInputIndex;
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;
    private ControllerSnapshot _physical = ControllerSnapshot.Disconnected;
    private ControllerSnapshot _output = ControllerSnapshot.Disconnected;
    private bool _m1Down;
    private bool _m2Down;
    private string? _fault;
    private bool _disposed;

    public VirtualControllerBackend(int physicalXInputIndex)
    {
        if (physicalXInputIndex is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(physicalXInputIndex));
        _physicalXInputIndex = physicalXInputIndex;
    }

    public event EventHandler<PaddleHookFaultEventArgs>? Faulted;

    public VirtualControllerRuntimeState GetRuntimeState()
    {
        lock (_gate)
            return new(_physicalXInputIndex, _physical, _output, _m1Down, _m2Down, _controller is not null, _fault);
    }

    public BackendStatus GetStatus()
    {
        lock (_gate)
        {
            if (_fault is not null)
                return new("Virtual Xbox 360", BackendHealth.Degraded, false, true,
                    $"Virtual output stopped safely: {_fault} Physical XInput index {_physicalXInputIndex + 1} remains pinned; coexistence still requires physical validation.");
            if (_controller is null)
                return new("Virtual Xbox 360", BackendHealth.Unavailable, false, true,
                    $"Virtual output is not connected. Physical XInput index {_physicalXInputIndex + 1} is reserved.");
            return new("Virtual Xbox 360", BackendHealth.Ready, true, true,
                $"Full mirrored virtual output is connected from pinned physical XInput index {_physicalXInputIndex + 1}. Physical and virtual controllers coexist; duplicate/slot behavior is not yet physically validated.");
        }
    }

    public Task<BackendStatus> InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_controller is not null) return Task.FromResult(GetStatus());
            try
            {
                _client = new ViGEmClient();
                _controller = _client.CreateXbox360Controller();
                _controller.AutoSubmitReport = false;
                _controller.Connect();
                SubmitLocked(ControllerSnapshot.Disconnected);
            }
            catch (Exception ex)
            {
                FailLocked(ex);
                throw;
            }
            return Task.FromResult(GetStatus());
        }
    }

    public Task<BackendApplyResult> ApplyAsync(MappingProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_controller is null) return Task.FromResult(new BackendApplyResult(false, GetStatus().Message, GetStatus()));
            try
            {
                _output = _pipeline.SetProfile(profile);
                SubmitLocked(_output);
                var status = GetStatus();
                return Task.FromResult(new BackendApplyResult(true, $"{profile.Name} is live on the mirrored virtual Xbox controller.", status));
            }
            catch (Exception ex)
            {
                FailLocked(ex);
                return Task.FromResult(new BackendApplyResult(false, GetStatus().Message, GetStatus()));
            }
        }
    }

    public Task<BackendApplyResult> RestoreDefaultAsync(CancellationToken cancellationToken = default) =>
        ApplyAsync(MappingProfile.Default, cancellationToken);

    public ControllerSnapshot ProcessSnapshot(ControllerSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_controller is null) return ControllerSnapshot.Disconnected;
            try
            {
                _physical = snapshot;
                _output = _pipeline.ProcessPhysical(snapshot);
                SubmitLocked(_output);
                return _output;
            }
            catch (Exception ex)
            {
                FailLocked(ex);
                return ControllerSnapshot.Disconnected;
            }
        }
    }

    public ControllerSnapshot SetPaddleState(ControllerButton paddle, bool isDown)
    {
        lock (_gate)
        {
            if (_controller is null) return ControllerSnapshot.Disconnected;
            try
            {
                if (paddle == ControllerButton.M1) _m1Down = isDown;
                else if (paddle == ControllerButton.M2) _m2Down = isDown;
                else throw new ArgumentOutOfRangeException(nameof(paddle));
                _output = _pipeline.SetRearPaddle(paddle, isDown);
                SubmitLocked(_output);
                return _output;
            }
            catch (Exception ex)
            {
                FailLocked(ex);
                return ControllerSnapshot.Disconnected;
            }
        }
    }

    public void EmergencyStop()
    {
        lock (_gate)
        {
            NeutralizeAndDisconnectLocked();
            _pipeline.Reset();
            _physical = ControllerSnapshot.Disconnected;
            _output = ControllerSnapshot.Disconnected;
            _m1Down = false;
            _m2Down = false;
        }
    }

    public Task EmergencyStopAsync()
    {
        EmergencyStop();
        return Task.CompletedTask;
    }

    private void SubmitLocked(ControllerSnapshot snapshot)
    {
        if (_controller is null) return;
        var connected = snapshot.IsConnected;
        foreach (var (source, target) in ButtonMap)
            _controller.SetButtonState(target, connected && snapshot.Buttons.HasFlag(source));
        _controller.SetSliderValue(Xbox360Slider.LeftTrigger, connected ? snapshot.LeftTrigger : (byte)0);
        _controller.SetSliderValue(Xbox360Slider.RightTrigger, connected ? snapshot.RightTrigger : (byte)0);
        _controller.SetAxisValue(Xbox360Axis.LeftThumbX, connected ? snapshot.LeftThumbX : (short)0);
        _controller.SetAxisValue(Xbox360Axis.LeftThumbY, connected ? snapshot.LeftThumbY : (short)0);
        _controller.SetAxisValue(Xbox360Axis.RightThumbX, connected ? snapshot.RightThumbX : (short)0);
        _controller.SetAxisValue(Xbox360Axis.RightThumbY, connected ? snapshot.RightThumbY : (short)0);
        _controller.SubmitReport();
    }

    private void FailLocked(Exception exception)
    {
        if (_fault is not null) return;
        _fault = exception.Message;
        NeutralizeAndDisconnectLocked();
        ThreadPool.QueueUserWorkItem(_ => Faulted?.Invoke(this, new PaddleHookFaultEventArgs(exception)));
    }

    private void NeutralizeAndDisconnectLocked()
    {
        if (_controller is not null)
        {
            try { SubmitLocked(ControllerSnapshot.Disconnected); } catch { }
            try { _controller.Disconnect(); } catch { }
            _controller = null;
        }
        try { _client?.Dispose(); } catch { }
        _client = null;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            NeutralizeAndDisconnectLocked();
            _pipeline.Reset();
        }
        return ValueTask.CompletedTask;
    }
}
