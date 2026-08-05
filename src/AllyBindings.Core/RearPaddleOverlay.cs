namespace AllyBindings.Core;

public static class RearPaddleOverlay
{
    private const ControllerButton RearMask = ControllerButton.M1 | ControllerButton.M2;

    public static ControllerSnapshot Apply(ControllerSnapshot input, bool m1Down, bool m2Down)
    {
        if (!input.IsConnected)
        {
            return ControllerSnapshot.Disconnected;
        }

        var buttons = input.Buttons & ~RearMask;
        if (m1Down) buttons |= ControllerButton.M1;
        if (m2Down) buttons |= ControllerButton.M2;
        return input with { Buttons = buttons };
    }
}
