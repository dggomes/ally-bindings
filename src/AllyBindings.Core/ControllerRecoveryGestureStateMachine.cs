namespace AllyBindings.Core;

/// <summary>
/// Immutable fixed recovery recognizer. It is deliberately independent from
/// configurable profile rotation so a broken profile cannot remove recovery.
/// </summary>
public sealed class ControllerRecoveryGestureStateMachine
{
    public const int ChordHoldMilliseconds = 750;
    public const int LeftTriggerHoldMilliseconds = 1250;
    public const byte LeftTriggerThreshold = 128;
    private const int StickNeutralDeadzone = 10_000;
    private const byte TriggerNeutralThreshold = 8;

    private static readonly ControllerButton RecoveryChord = ControllerButton.View | ControllerButton.Menu;
    private RecoveryPhase _phase = RecoveryPhase.WaitingForNeutral;
    private DateTimeOffset? _phaseStartedAt;
    private bool _attemptStarted;

    /// <summary>
    /// True after LT is pressed as part of a recovery attempt and until the
    /// controller returns fully neutral. Callers should reserve the sample so
    /// an aborted recovery cannot accidentally commit a profile selection.
    /// </summary>
    public bool IsConsumingInput => _attemptStarted;

    public bool Process(ControllerSnapshot snapshot, DateTimeOffset now)
    {
        if (!snapshot.IsConnected)
        {
            Reset();
            return false;
        }

        if (_phase == RecoveryPhase.WaitingForNeutral)
        {
            if (IsFullyNeutral(snapshot)) ArmAfterNeutral();
            return false;
        }

        var exactChord = snapshot.Buttons == RecoveryChord;
        // Real handheld sticks rarely report mathematically exact zero. Recovery
        // must tolerate ordinary drift while deliberate movement still cancels it.
        var axesNeutral = snapshot.RightTrigger <= TriggerNeutralThreshold &&
                          Math.Abs((int)snapshot.LeftThumbX) <= StickNeutralDeadzone &&
                          Math.Abs((int)snapshot.LeftThumbY) <= StickNeutralDeadzone &&
                          Math.Abs((int)snapshot.RightThumbX) <= StickNeutralDeadzone &&
                          Math.Abs((int)snapshot.RightThumbY) <= StickNeutralDeadzone;
        if (!exactChord || !axesNeutral)
        {
            InvalidateUnlessNeutral(snapshot);
            return false;
        }

        switch (_phase)
        {
            case RecoveryPhase.Idle when snapshot.LeftTrigger <= TriggerNeutralThreshold:
                _phase = RecoveryPhase.HoldingChord;
                _phaseStartedAt = now;
                return false;

            case RecoveryPhase.HoldingChord:
                if (snapshot.LeftTrigger > TriggerNeutralThreshold)
                {
                    _attemptStarted = true;
                    if (!_phaseStartedAt.HasValue ||
                        now - _phaseStartedAt.Value < TimeSpan.FromMilliseconds(ChordHoldMilliseconds) ||
                        snapshot.LeftTrigger < LeftTriggerThreshold)
                    {
                        _phase = RecoveryPhase.WaitingForNeutral;
                        _phaseStartedAt = null;
                        return false;
                    }

                    _phase = RecoveryPhase.HoldingLeftTrigger;
                    _phaseStartedAt = now;
                }
                return false;

            case RecoveryPhase.HoldingLeftTrigger:
                if (snapshot.LeftTrigger < LeftTriggerThreshold)
                {
                    _phase = RecoveryPhase.WaitingForNeutral;
                    _phaseStartedAt = null;
                    return false;
                }
                if (_phaseStartedAt.HasValue &&
                    now - _phaseStartedAt.Value >= TimeSpan.FromMilliseconds(LeftTriggerHoldMilliseconds))
                {
                    _phase = RecoveryPhase.WaitingForNeutral;
                    _phaseStartedAt = null;
                    return true;
                }
                return false;

            default:
                _phase = RecoveryPhase.WaitingForNeutral;
                _phaseStartedAt = null;
                return false;
        }
    }

    public void Reset()
    {
        _phase = RecoveryPhase.WaitingForNeutral;
        _phaseStartedAt = null;
        _attemptStarted = false;
    }

    private void ArmAfterNeutral()
    {
        _phase = RecoveryPhase.Idle;
        _phaseStartedAt = null;
        _attemptStarted = false;
    }

    private void InvalidateUnlessNeutral(ControllerSnapshot snapshot)
    {
        if (IsFullyNeutral(snapshot)) ArmAfterNeutral();
        else
        {
            _phase = RecoveryPhase.WaitingForNeutral;
            _phaseStartedAt = null;
        }
    }

    private static bool IsFullyNeutral(ControllerSnapshot snapshot) =>
        snapshot.Buttons == ControllerButton.None &&
        snapshot.LeftTrigger <= TriggerNeutralThreshold &&
        snapshot.RightTrigger <= TriggerNeutralThreshold &&
        Math.Abs((int)snapshot.LeftThumbX) <= StickNeutralDeadzone &&
        Math.Abs((int)snapshot.LeftThumbY) <= StickNeutralDeadzone &&
        Math.Abs((int)snapshot.RightThumbX) <= StickNeutralDeadzone &&
        Math.Abs((int)snapshot.RightThumbY) <= StickNeutralDeadzone;

    private enum RecoveryPhase
    {
        Idle,
        HoldingChord,
        HoldingLeftTrigger,
        WaitingForNeutral,
    }
}
