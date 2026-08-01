using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class ControllerInputArbitrationTests
{
    [Theory]
    [InlineData(ControllerButton.A | ControllerButton.B)]
    [InlineData(ControllerButton.X | ControllerButton.Y)]
    [InlineData(ControllerButton.LeftBumper | ControllerButton.RightBumper)]
    public void Active_editor_neutralizes_supported_shortcut_chords(ControllerButton chord)
    {
        var snapshot = new ControllerSnapshot(true, chord, LeftTrigger: 255, RightTrigger: 255);

        var routed = ControllerInputArbitration.ForProfileCycle(snapshot, editorConsumed: true);

        Assert.True(routed.IsConnected);
        Assert.Equal(ControllerButton.None, routed.Buttons);
        Assert.Equal(0, routed.LeftTrigger);
        Assert.Equal(0, routed.RightTrigger);
    }

    [Fact]
    public void Hidden_editor_preserves_profile_cycle_input()
    {
        var snapshot = new ControllerSnapshot(true, ControllerButton.View | ControllerButton.Menu, RightTrigger: 255);

        var routed = ControllerInputArbitration.ForProfileCycle(snapshot, editorConsumed: false);

        Assert.Equal(snapshot, routed);
    }
}
