using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class ControllerRecoveryGestureStateMachineTests
{
    private static readonly ControllerButton Chord = ControllerButton.View | ControllerButton.Menu;
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-05T12:00:00Z");

    [Fact]
    public void Exact_chord_then_new_left_trigger_hold_fires_once()
    {
        var machine = NewArmed();
        Assert.False(machine.Process(new(true, Chord), T0));
        Assert.False(machine.Process(new(true, Chord), T0.AddMilliseconds(750)));
        Assert.False(machine.Process(new(true, Chord, LeftTrigger: 128), T0.AddMilliseconds(751)));
        Assert.False(machine.Process(new(true, Chord, LeftTrigger: 255), T0.AddMilliseconds(2000)));
        Assert.True(machine.Process(new(true, Chord, LeftTrigger: 255), T0.AddMilliseconds(2001)));
        Assert.False(machine.Process(new(true, Chord, LeftTrigger: 255), T0.AddMilliseconds(4000)));
    }

    [Fact]
    public void Requires_full_neutral_release_before_rearming()
    {
        var machine = Fire();
        Assert.False(machine.Process(new(true, ControllerButton.View), T0.AddSeconds(4)));
        Assert.False(machine.Process(new(true, Chord), T0.AddSeconds(5)));
        Assert.False(machine.Process(new(true, ControllerButton.None), T0.AddSeconds(6)));
        Assert.False(machine.Process(new(true, Chord), T0.AddSeconds(7)));
        Assert.False(machine.Process(new(true, Chord), T0.AddMilliseconds(7750)));
        Assert.False(machine.Process(new(true, Chord, LeftTrigger: 255), T0.AddMilliseconds(7751)));
        Assert.True(machine.Process(new(true, Chord, LeftTrigger: 255), T0.AddMilliseconds(9001)));
    }

    [Fact]
    public void Rejects_early_or_preheld_trigger_and_extra_controls()
    {
        var early = NewArmed();
        early.Process(new(true, Chord), T0);
        Assert.False(early.Process(new(true, Chord, LeftTrigger: 255), T0.AddMilliseconds(749)));
        Assert.False(early.Process(new(true, Chord, LeftTrigger: 255), T0.AddSeconds(3)));

        var preheld = NewArmed();
        Assert.False(preheld.Process(new(true, Chord, LeftTrigger: 255), T0));
        Assert.False(preheld.Process(new(true, Chord, LeftTrigger: 255), T0.AddSeconds(3)));

        var extra = NewArmed();
        extra.Process(new(true, Chord), T0);
        extra.Process(new(true, Chord), T0.AddMilliseconds(750));
        Assert.False(extra.Process(new(true, Chord | ControllerButton.A, LeftTrigger: 255), T0.AddMilliseconds(751)));
        Assert.False(extra.Process(new(true, Chord, LeftTrigger: 255), T0.AddSeconds(3)));
    }

    [Fact]
    public void Disconnect_resets_without_firing()
    {
        var machine = NewArmed();
        machine.Process(new(true, Chord), T0);
        machine.Process(new(true, Chord), T0.AddMilliseconds(750));
        machine.Process(new(true, Chord, LeftTrigger: 255), T0.AddMilliseconds(751));
        Assert.False(machine.Process(ControllerSnapshot.Disconnected, T0.AddSeconds(3)));
        Assert.False(machine.Process(new(true, Chord, LeftTrigger: 255), T0.AddSeconds(4)));
    }

    [Fact]
    public void Ordinary_stick_drift_does_not_make_recovery_impossible()
    {
        var machine = NewArmed();
        var chordWithDrift = new ControllerSnapshot(
            true,
            Chord,
            LeftThumbX: 2_500,
            RightThumbY: -3_000);

        Assert.False(machine.Process(chordWithDrift, T0));
        Assert.False(machine.Process(chordWithDrift, T0.AddMilliseconds(750)));
        Assert.False(machine.Process(chordWithDrift with { LeftTrigger = 255 }, T0.AddMilliseconds(751)));
        Assert.True(machine.Process(chordWithDrift with { LeftTrigger = 255 }, T0.AddMilliseconds(2001)));
    }

    [Fact]
    public void Deliberate_stick_movement_cancels_recovery()
    {
        var machine = NewArmed();
        machine.Process(new(true, Chord), T0);
        machine.Process(new(true, Chord), T0.AddMilliseconds(750));

        Assert.False(machine.Process(
            new(true, Chord, LeftTrigger: 255, LeftThumbX: 12_000),
            T0.AddMilliseconds(751)));
        Assert.False(machine.Process(new(true, Chord, LeftTrigger: 255), T0.AddSeconds(3)));
    }

    [Fact]
    public void Lt_attempt_reserves_input_until_full_neutral_even_when_aborted()
    {
        var machine = NewArmed();

        Assert.False(machine.Process(new(true, Chord), T0));
        Assert.False(machine.IsConsumingInput);
        Assert.False(machine.Process(new(true, Chord, LeftTrigger: 255), T0.AddMilliseconds(750)));
        Assert.True(machine.IsConsumingInput);
        Assert.False(machine.Process(new(true, Chord), T0.AddMilliseconds(900)));
        Assert.True(machine.IsConsumingInput);
        Assert.False(machine.Process(new(true, ControllerButton.None), T0.AddMilliseconds(950)));
        Assert.False(machine.IsConsumingInput);
    }

    [Fact]
    public void Trigger_noise_within_neutral_threshold_permits_recovery()
    {
        var machine = NewArmed();
        Assert.False(machine.Process(new(true, Chord, LeftTrigger: 1), T0));
        Assert.False(machine.Process(new(true, Chord, LeftTrigger: 8), T0.AddMilliseconds(750)));
        Assert.False(machine.Process(new(true, Chord, LeftTrigger: 128), T0.AddMilliseconds(751)));
        Assert.True(machine.Process(new(true, Chord, LeftTrigger: 255), T0.AddMilliseconds(2001)));
    }

    [Fact]
    public void Startup_and_reconnect_require_neutral_before_a_preheld_chord()
    {
        var machine = new ControllerRecoveryGestureStateMachine();
        Assert.False(machine.Process(new(true, Chord), T0));
        Assert.False(machine.Process(new(true, Chord), T0.AddSeconds(2)));
        Assert.False(machine.Process(new(true, Chord, LeftTrigger: 255), T0.AddSeconds(3)));

        Assert.False(machine.Process(new(true, ControllerButton.None), T0.AddSeconds(4)));
        Assert.False(machine.Process(new(true, Chord), T0.AddSeconds(5)));
        Assert.False(machine.Process(ControllerSnapshot.Disconnected, T0.AddSeconds(6)));
        Assert.False(machine.Process(new(true, Chord), T0.AddSeconds(7)));
        Assert.False(machine.Process(new(true, Chord, LeftTrigger: 255), T0.AddSeconds(9)));
    }

    private static ControllerRecoveryGestureStateMachine NewArmed()
    {
        var machine = new ControllerRecoveryGestureStateMachine();
        machine.Process(new(true, ControllerButton.None), T0.AddMilliseconds(-1));
        return machine;
    }

    private static ControllerRecoveryGestureStateMachine Fire()
    {
        var machine = NewArmed();
        machine.Process(new(true, Chord), T0);
        machine.Process(new(true, Chord), T0.AddMilliseconds(750));
        machine.Process(new(true, Chord, LeftTrigger: 255), T0.AddMilliseconds(751));
        Assert.True(machine.Process(new(true, Chord, LeftTrigger: 255), T0.AddMilliseconds(2001)));
        return machine;
    }
}
