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
        foreach (var button in ControllerButtons.Mappable)
        {
            if ((input.Buttons & button) == 0)
            {
                continue;
            }

            mappedButtons |= profile.Bindings.GetValueOrDefault(button, button);
        }

        return input with { Buttons = mappedButtons };
    }
}
