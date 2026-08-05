using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class RearPaddleOverlayTests
{
    [Fact]
    public void Overlay_adds_current_rear_state_without_changing_physical_axes_or_buttons()
    {
        var input = new ControllerSnapshot(
            true,
            ControllerButton.A,
            LeftTrigger: 17,
            RightThumbY: -1234);

        var output = RearPaddleOverlay.Apply(input, m1Down: true, m2Down: false);

        Assert.Equal(ControllerButton.A | ControllerButton.M1, output.Buttons);
        Assert.Equal(17, output.LeftTrigger);
        Assert.Equal(-1234, output.RightThumbY);
    }

    [Fact]
    public void Overlay_replaces_stale_rear_bits_with_the_authoritative_key_state()
    {
        var input = new ControllerSnapshot(
            true,
            ControllerButton.M1 | ControllerButton.M2 | ControllerButton.X);

        var output = RearPaddleOverlay.Apply(input, m1Down: false, m2Down: true);

        Assert.Equal(ControllerButton.M2 | ControllerButton.X, output.Buttons);
    }

    [Fact]
    public void Overlay_does_not_create_input_for_a_disconnected_controller()
    {
        Assert.Equal(
            ControllerSnapshot.Disconnected,
            RearPaddleOverlay.Apply(ControllerSnapshot.Disconnected, m1Down: true, m2Down: true));
    }
}
