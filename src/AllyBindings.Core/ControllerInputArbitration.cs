namespace AllyBindings.Core;

/// <summary>
/// Keeps controller-first window navigation and background profile cycling mutually exclusive.
/// When the editor owns controller input, the cycle state machine receives a connected but
/// neutral snapshot so any armed shortcut is cancelled without leaking UI buttons into a chord.
/// </summary>
public static class ControllerInputArbitration
{
    public static ControllerSnapshot ForProfileCycle(ControllerSnapshot snapshot, bool editorConsumed)
    {
        return editorConsumed
            ? snapshot with { Buttons = ControllerButton.None, LeftTrigger = 0, RightTrigger = 0 }
            : snapshot;
    }
}
