namespace AllyBindings.Core;

public sealed class RealtimeControllerPipeline
{
    private readonly object _gate = new();
    private MappingProfile _profile = MappingProfile.Default;
    private ControllerSnapshot _lastPhysical = ControllerSnapshot.Disconnected;
    private bool _m1Down;
    private bool _m2Down;

    public string ActiveProfileId
    {
        get
        {
            lock (_gate) return _profile.Id;
        }
    }

    public ControllerSnapshot SetProfile(MappingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            _profile = profile;
            return RenderLocked();
        }
    }

    public ControllerSnapshot ProcessPhysical(ControllerSnapshot snapshot)
    {
        lock (_gate)
        {
            _lastPhysical = snapshot;
            if (!snapshot.IsConnected)
            {
                _m1Down = false;
                _m2Down = false;
            }
            return RenderLocked();
        }
    }

    public ControllerSnapshot SetRearPaddle(ControllerButton paddle, bool isDown)
    {
        lock (_gate)
        {
            switch (paddle)
            {
                case ControllerButton.M1:
                    _m1Down = isDown;
                    break;
                case ControllerButton.M2:
                    _m2Down = isDown;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(paddle), "Only M1 and M2 are rear-paddle inputs.");
            }
            return RenderLocked();
        }
    }

    public ControllerSnapshot Reset()
    {
        lock (_gate)
        {
            _profile = MappingProfile.Default;
            _m1Down = false;
            _m2Down = false;
            _lastPhysical = ControllerSnapshot.Disconnected;
            return ControllerSnapshot.Disconnected;
        }
    }

    private ControllerSnapshot RenderLocked()
    {
        var withPaddles = RearPaddleOverlay.Apply(_lastPhysical, _m1Down, _m2Down);
        return MappingEngine.Apply(withPaddles, _profile);
    }
}
