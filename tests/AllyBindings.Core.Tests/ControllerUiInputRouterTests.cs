using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class ControllerUiInputRouterTests
{
    [Fact]
    public void Emits_commands_only_on_button_edges()
    {
        var router = new ControllerUiInputRouter();
        var held = new ControllerSnapshot(true, ControllerButton.DPadDown | ControllerButton.A);

        Assert.Equal(
            [ControllerUiCommand.MoveDown, ControllerUiCommand.Activate],
            router.Process(held));
        Assert.Empty(router.Process(held));
        Assert.Empty(router.Process(new ControllerSnapshot(true, ControllerButton.None)));
        Assert.Equal(
            [ControllerUiCommand.MoveDown, ControllerUiCommand.Activate],
            router.Process(held));
    }

    [Fact]
    public void Maps_section_and_primary_action_buttons()
    {
        var router = new ControllerUiInputRouter();
        var snapshot = new ControllerSnapshot(
            true,
            ControllerButton.LeftBumper |
            ControllerButton.RightBumper |
            ControllerButton.B |
            ControllerButton.X |
            ControllerButton.Y);

        Assert.Equal(
            [
                ControllerUiCommand.PreviousSection,
                ControllerUiCommand.NextSection,
                ControllerUiCommand.Back,
                ControllerUiCommand.Save,
                ControllerUiCommand.Apply,
            ],
            router.Process(snapshot));
    }

    [Fact]
    public void Disconnect_resets_edge_state()
    {
        var router = new ControllerUiInputRouter();
        var pressed = new ControllerSnapshot(true, ControllerButton.A);

        Assert.Single(router.Process(pressed));
        Assert.Empty(router.Process(ControllerSnapshot.Disconnected));
        Assert.Equal([ControllerUiCommand.Activate], router.Process(pressed));
    }

    [Fact]
    public void View_and_menu_are_reserved_for_profile_cycle_chord()
    {
        var router = new ControllerUiInputRouter();

        Assert.Empty(router.Process(new ControllerSnapshot(
            true,
            ControllerButton.View | ControllerButton.Menu)));
    }
}
