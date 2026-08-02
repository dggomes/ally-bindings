using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class ControllerInputArbitrationTests
{
    [Theory]
    [InlineData(ControllerButton.A | ControllerButton.B)]
    [InlineData(ControllerButton.X | ControllerButton.Y)]
    [InlineData(ControllerButton.LeftBumper | ControllerButton.RightBumper)]
    public void Entering_editor_cancels_and_suppresses_supported_shortcut_chords(ControllerButton chord)
    {
        var arbitration = new ControllerInputArbitration();
        var snapshot = new ControllerSnapshot(true, chord, LeftTrigger: 255, RightTrigger: 255);

        var routed = arbitration.Route(snapshot, editorConsumed: true);

        Assert.True(routed.CancelCycle);
        Assert.False(routed.ShouldProcess);
        Assert.Equal(ControllerButton.None, routed.Snapshot.Buttons);
        Assert.Equal(0, routed.Snapshot.LeftTrigger);
        Assert.Equal(0, routed.Snapshot.RightTrigger);
    }

    [Fact]
    public void Existing_pending_cycle_is_cancelled_when_editor_takes_ownership()
    {
        var arbitration = new ControllerInputArbitration();
        var neutral = new ControllerSnapshot(true, ControllerButton.None);
        var chord = new ControllerSnapshot(true, ControllerButton.View | ControllerButton.Menu);

        Assert.True(arbitration.Route(neutral, editorConsumed: false).ShouldProcess);
        var entered = arbitration.Route(chord, editorConsumed: true);
        var remainsOpen = arbitration.Route(neutral, editorConsumed: true);

        Assert.True(entered.CancelCycle);
        Assert.False(entered.ShouldProcess);
        Assert.False(remainsOpen.CancelCycle);
        Assert.False(remainsOpen.ShouldProcess);
    }

    [Fact]
    public void Cycling_requires_full_button_release_after_editor_closes()
    {
        var arbitration = new ControllerInputArbitration();
        var chord = new ControllerSnapshot(true, ControllerButton.View | ControllerButton.Menu);
        var neutral = new ControllerSnapshot(true, ControllerButton.None);

        arbitration.Route(chord, editorConsumed: true);
        var stillHeld = arbitration.Route(chord, editorConsumed: false);
        var released = arbitration.Route(neutral, editorConsumed: false);
        var rearmed = arbitration.Route(chord, editorConsumed: false);

        Assert.False(stillHeld.ShouldProcess);
        Assert.True(stillHeld.CancelCycle);
        Assert.True(released.ShouldProcess);
        Assert.True(released.CancelCycle);
        Assert.True(rearmed.ShouldProcess);
        Assert.False(rearmed.CancelCycle);
        Assert.Equal(chord, rearmed.Snapshot);
    }

    [Fact]
    public void Hidden_editor_preserves_profile_cycle_input()
    {
        var arbitration = new ControllerInputArbitration();
        var snapshot = new ControllerSnapshot(true, ControllerButton.View | ControllerButton.Menu, RightTrigger: 255);

        var routed = arbitration.Route(snapshot, editorConsumed: false);

        Assert.True(routed.ShouldProcess);
        Assert.False(routed.CancelCycle);
        Assert.Equal(snapshot, routed.Snapshot);
    }
}
