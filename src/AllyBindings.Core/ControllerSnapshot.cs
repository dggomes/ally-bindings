namespace AllyBindings.Core;

public readonly record struct ControllerSnapshot(
    bool IsConnected,
    ControllerButton Buttons,
    byte LeftTrigger = 0,
    byte RightTrigger = 0,
    short LeftThumbX = 0,
    short LeftThumbY = 0,
    short RightThumbX = 0,
    short RightThumbY = 0)
{
    public static ControllerSnapshot Disconnected => new(false, ControllerButton.None);
}
