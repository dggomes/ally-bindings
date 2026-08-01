namespace AllyBindings.Core;

public enum ControllerUiCommand
{
    MoveUp,
    MoveDown,
    MoveLeft,
    MoveRight,
    PreviousSection,
    NextSection,
    Activate,
    Back,
    Save,
    Apply,
}

/// <summary>
/// Converts XInput button edges into UI commands. Holding a control never
/// generates a click storm: the user must release it before it can fire again.
/// </summary>
public sealed class ControllerUiInputRouter
{
    private ControllerButton _previousButtons;

    public IReadOnlyList<ControllerUiCommand> Process(ControllerSnapshot snapshot)
    {
        if (!snapshot.IsConnected)
        {
            _previousButtons = ControllerButton.None;
            return [];
        }

        var pressed = snapshot.Buttons & ~_previousButtons;
        _previousButtons = snapshot.Buttons;
        if (pressed == ControllerButton.None)
        {
            return [];
        }

        var commands = new List<ControllerUiCommand>(4);
        AddIfPressed(pressed, ControllerButton.DPadUp, ControllerUiCommand.MoveUp, commands);
        AddIfPressed(pressed, ControllerButton.DPadDown, ControllerUiCommand.MoveDown, commands);
        AddIfPressed(pressed, ControllerButton.DPadLeft, ControllerUiCommand.MoveLeft, commands);
        AddIfPressed(pressed, ControllerButton.DPadRight, ControllerUiCommand.MoveRight, commands);
        AddIfPressed(pressed, ControllerButton.LeftBumper, ControllerUiCommand.PreviousSection, commands);
        AddIfPressed(pressed, ControllerButton.RightBumper, ControllerUiCommand.NextSection, commands);
        AddIfPressed(pressed, ControllerButton.A, ControllerUiCommand.Activate, commands);
        AddIfPressed(pressed, ControllerButton.B, ControllerUiCommand.Back, commands);
        AddIfPressed(pressed, ControllerButton.X, ControllerUiCommand.Save, commands);
        AddIfPressed(pressed, ControllerButton.Y, ControllerUiCommand.Apply, commands);
        return commands;
    }

    private static void AddIfPressed(
        ControllerButton pressed,
        ControllerButton button,
        ControllerUiCommand command,
        ICollection<ControllerUiCommand> commands)
    {
        if ((pressed & button) != 0)
        {
            commands.Add(command);
        }
    }
}
