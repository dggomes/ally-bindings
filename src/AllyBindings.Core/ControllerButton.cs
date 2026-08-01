namespace AllyBindings.Core;

[Flags]
public enum ControllerButton : ushort
{
    None = 0,
    DPadUp = 1 << 0,
    DPadDown = 1 << 1,
    DPadLeft = 1 << 2,
    DPadRight = 1 << 3,
    Menu = 1 << 4,
    View = 1 << 5,
    LeftStick = 1 << 6,
    RightStick = 1 << 7,
    LeftBumper = 0x0100,
    RightBumper = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

public static class ControllerButtons
{
    public static readonly ControllerButton[] Mappable =
    [
        ControllerButton.DPadUp,
        ControllerButton.DPadDown,
        ControllerButton.DPadLeft,
        ControllerButton.DPadRight,
        ControllerButton.Menu,
        ControllerButton.View,
        ControllerButton.LeftStick,
        ControllerButton.RightStick,
        ControllerButton.LeftBumper,
        ControllerButton.RightBumper,
        ControllerButton.A,
        ControllerButton.B,
        ControllerButton.X,
        ControllerButton.Y,
    ];

    public static ControllerButton MappableMask { get; } = Mappable.Aggregate(
        ControllerButton.None,
        static (mask, button) => mask | button);

    public static bool IsExactChord(this ControllerButton pressed, IEnumerable<ControllerButton> required)
    {
        var requiredMask = required.Aggregate(
            ControllerButton.None,
            static (mask, button) => mask | button);
        return requiredMask != ControllerButton.None && (pressed & MappableMask) == requiredMask;
    }

    public static bool ContainsAll(this ControllerButton pressed, IEnumerable<ControllerButton> required)
    {
        foreach (var button in required)
        {
            if (button == ControllerButton.None || (pressed & button) != button)
            {
                return false;
            }
        }

        return true;
    }
}
