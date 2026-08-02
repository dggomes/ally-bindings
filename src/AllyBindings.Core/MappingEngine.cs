namespace AllyBindings.Core;

public static class MappingEngine
{
    public const byte TriggerActivationThreshold = 30;

    public static ControllerSnapshot Apply(ControllerSnapshot input, MappingProfile profile)
    {
        if (!input.IsConnected || profile.Bindings.Count == 0)
        {
            return input;
        }

        var mappedButtons = input.Buttons & ~ControllerButtons.MappableMask;
        byte leftTrigger = 0;
        byte rightTrigger = 0;

        foreach (var button in ControllerButtons.DigitalMappableSources)
        {
            if ((input.Buttons & button) == 0)
            {
                continue;
            }

            Project(
                profile.Bindings.GetValueOrDefault(button, button),
                byte.MaxValue,
                ref mappedButtons,
                ref leftTrigger,
                ref rightTrigger);
        }

        Project(
            profile.Bindings.GetValueOrDefault(ControllerButton.LeftTrigger, ControllerButton.LeftTrigger),
            input.LeftTrigger,
            ref mappedButtons,
            ref leftTrigger,
            ref rightTrigger);
        Project(
            profile.Bindings.GetValueOrDefault(ControllerButton.RightTrigger, ControllerButton.RightTrigger),
            input.RightTrigger,
            ref mappedButtons,
            ref leftTrigger,
            ref rightTrigger);

        return input with { Buttons = mappedButtons, LeftTrigger = leftTrigger, RightTrigger = rightTrigger };
    }

    private static void Project(
        ControllerButton target,
        byte intensity,
        ref ControllerButton mappedButtons,
        ref byte leftTrigger,
        ref byte rightTrigger)
    {
        if (target == ControllerButton.LeftTrigger)
        {
            leftTrigger = Math.Max(leftTrigger, intensity);
        }
        else if (target == ControllerButton.RightTrigger)
        {
            rightTrigger = Math.Max(rightTrigger, intensity);
        }
        else if (intensity >= TriggerActivationThreshold)
        {
            mappedButtons |= target;
        }
    }
}
