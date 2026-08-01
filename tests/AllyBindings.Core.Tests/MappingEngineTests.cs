using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class MappingEngineTests
{
    [Fact]
    public void Button_values_match_XInput_button_mask()
    {
        Assert.Equal(0x0010, (ushort)ControllerButton.Menu);
        Assert.Equal(0x0020, (ushort)ControllerButton.View);
        Assert.Equal(0x0100, (ushort)ControllerButton.LeftBumper);
        Assert.Equal(0x0200, (ushort)ControllerButton.RightBumper);
        Assert.Equal(0x1000, (ushort)ControllerButton.A);
        Assert.Equal(0x2000, (ushort)ControllerButton.B);
        Assert.Equal(0x4000, (ushort)ControllerButton.X);
        Assert.Equal(0x8000, (ushort)ControllerButton.Y);
    }

    [Fact]
    public void Apply_preserves_unknown_input_bits()
    {
        const ControllerButton guideOrReservedBit = (ControllerButton)0x0400;
        var input = new ControllerSnapshot(true, ControllerButton.A | guideOrReservedBit);
        var profile = new MappingProfile
        {
            Id = "mapped",
            Name = "Mapped",
            Bindings = new() { [ControllerButton.A] = ControllerButton.B },
        };

        var output = MappingEngine.Apply(input, profile);

        Assert.True(output.Buttons.HasFlag(guideOrReservedBit));
        Assert.True(output.Buttons.HasFlag(ControllerButton.B));
    }

    [Fact]
    public void Apply_maps_buttons_and_preserves_axes()
    {
        var input = new ControllerSnapshot(
            true,
            ControllerButton.A | ControllerButton.LeftBumper,
            LeftTrigger: 17,
            LeftThumbX: 1234);
        var profile = new MappingProfile
        {
            Id = "test",
            Name = "Test",
            Bindings = new Dictionary<ControllerButton, ControllerButton>
            {
                [ControllerButton.A] = ControllerButton.B,
            },
        };

        var output = MappingEngine.Apply(input, profile);

        Assert.Equal(ControllerButton.B | ControllerButton.LeftBumper, output.Buttons);
        Assert.Equal(17, output.LeftTrigger);
        Assert.Equal(1234, output.LeftThumbX);
    }

    [Fact]
    public void Apply_is_identity_for_default_profile()
    {
        var input = new ControllerSnapshot(true, ControllerButton.X, RightThumbY: -42);
        Assert.Equal(input, MappingEngine.Apply(input, MappingProfile.Default));
    }
}
