namespace AllyBindings.Core;

public static class MappingEngine
{
    public static ControllerSnapshot Apply(ControllerSnapshot input, MappingProfile profile)
    {
        if (!input.IsConnected || profile.Bindings.Count == 0)
        {
            return input;
        }

        var mappedButtons = input.Buttons & ~ControllerButtons.MappableMask;
        var leftTrigger = input.LeftTrigger;
        var rightTrigger = input.RightTrigger;
        foreach (var button in ControllerButtons.MappableSources)
        {
            if ((input.Buttons & button) == 0)
            {
                continue;
            }

            var target = profile.Bindings.GetValueOrDefault(button, button);
            if (target == ControllerButton.LeftTrigger)
            {
                leftTrigger = byte.MaxValue;
            }
            else if (target == ControllerButton.RightTrigger)
            {
                rightTrigger = byte.MaxValue;
            }
            else
            {
                mappedButtons |= target;
            }
        }

        return input with { Buttons = mappedButtons, LeftTrigger = leftTrigger, RightTrigger = rightTrigger };
    }
}
