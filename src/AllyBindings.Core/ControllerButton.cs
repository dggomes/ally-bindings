namespace AllyBindings.Core;

[Flags]
public enum ControllerButton : uint
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
    LeftTrigger = 1u << 16,
    RightTrigger = 1u << 17,
    M1 = 1u << 18,
    M2 = 1u << 19,
}

public static class ControllerButtons
{
    public static readonly ControllerButton[] StandardButtons =
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

    public static readonly ControllerButton[] RearButtons =
    [
        ControllerButton.M1,
        ControllerButton.M2,
    ];

    public static readonly ControllerButton[] TriggerSources =
    [
        ControllerButton.LeftTrigger,
        ControllerButton.RightTrigger,
    ];

    public static readonly ControllerButton[] DigitalMappableSources = [.. StandardButtons, .. RearButtons];
    public static readonly ControllerButton[] MappableSources = [.. StandardButtons, .. TriggerSources, .. RearButtons];

    public static readonly ControllerButton[] OutputTargets =
    [
        .. StandardButtons,
        .. TriggerSources,
    ];

    // Shortcut chords are observed through XInput. M1/M2 and the analog
    // triggers are intentionally absent because XInput does not expose the rear
    // paddles and treating trigger thresholds as chord buttons would be unsafe.
    public static readonly ControllerButton[] ShortcutButtons = StandardButtons;

    public static bool IsValidBinding(ControllerButton source, ControllerButton target) =>
        MappableSources.Contains(source) &&
        (OutputTargets.Contains(target) || (RearButtons.Contains(source) && target == source));

    public static ControllerButton MappableMask { get; } = DigitalMappableSources.Aggregate(
        ControllerButton.None,
        static (mask, button) => mask | button);

    public static ControllerButton ShortcutMask { get; } = ShortcutButtons.Aggregate(
        ControllerButton.None,
        static (mask, button) => mask | button);

    public static bool IsExactChord(this ControllerButton pressed, IEnumerable<ControllerButton> required)
    {
        var requiredMask = required.Aggregate(
            ControllerButton.None,
            static (mask, button) => mask | button);
        return requiredMask != ControllerButton.None && (pressed & ShortcutMask) == requiredMask;
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
