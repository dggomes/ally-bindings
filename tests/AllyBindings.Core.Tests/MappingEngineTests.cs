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

    [Fact]
    public void Apply_can_project_a_rear_source_to_an_analog_trigger()
    {
        var input = new ControllerSnapshot(true, ControllerButton.M1, RightTrigger: 7);
        var profile = new MappingProfile
        {
            Id = "rear-trigger",
            Name = "Rear Trigger",
            Bindings = new() { [ControllerButton.M1] = ControllerButton.RightTrigger },
        };

        var output = MappingEngine.Apply(input, profile);

        Assert.Equal(ControllerButton.None, output.Buttons);
        Assert.Equal(byte.MaxValue, output.RightTrigger);
    }

    [Fact]
    public void Apply_can_map_a_physical_trigger_to_a_digital_button()
    {
        var input = new ControllerSnapshot(true, ControllerButton.None, LeftTrigger: 180);
        var profile = new MappingProfile
        {
            Id = "trigger-button",
            Name = "Trigger Button",
            Bindings = new() { [ControllerButton.LeftTrigger] = ControllerButton.X },
        };

        var output = MappingEngine.Apply(input, profile);

        Assert.Equal(ControllerButton.X, output.Buttons);
        Assert.Equal(0, output.LeftTrigger);
    }

    [Fact]
    public void Apply_does_not_activate_a_digital_target_below_the_trigger_threshold()
    {
        var input = new ControllerSnapshot(true, ControllerButton.None, LeftTrigger: 20);
        var profile = new MappingProfile
        {
            Id = "trigger-button",
            Name = "Trigger Button",
            Bindings = new() { [ControllerButton.LeftTrigger] = ControllerButton.X },
        };

        var output = MappingEngine.Apply(input, profile);

        Assert.Equal(ControllerButton.None, output.Buttons);
        Assert.Equal(0, output.LeftTrigger);
    }

    [Fact]
    public void Apply_preserves_trigger_intensity_when_mapped_to_the_other_trigger()
    {
        var input = new ControllerSnapshot(true, ControllerButton.None, LeftTrigger: 173, RightTrigger: 11);
        var profile = new MappingProfile
        {
            Id = "swap-trigger",
            Name = "Swap Trigger",
            Bindings = new() { [ControllerButton.LeftTrigger] = ControllerButton.RightTrigger },
        };

        var output = MappingEngine.Apply(input, profile);

        Assert.Equal(0, output.LeftTrigger);
        Assert.Equal(173, output.RightTrigger);
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, false)]
    [InlineData(31, true)]
    public void Trigger_to_digital_requires_value_above_xinput_threshold(byte intensity, bool expected)
    {
        var input = new ControllerSnapshot(true, ControllerButton.None, LeftTrigger: intensity);
        var profile = new MappingProfile
        {
            Id = "threshold",
            Name = "Threshold",
            Bindings = new() { [ControllerButton.LeftTrigger] = ControllerButton.A },
        };

        var output = MappingEngine.Apply(input, profile);

        Assert.Equal(expected, output.Buttons.HasFlag(ControllerButton.A));
    }

    [Fact]
    public void Right_trigger_uses_the_same_digital_projection_semantics()
    {
        var profile = new MappingProfile
        {
            Id = "right-threshold",
            Name = "Right threshold",
            Bindings = new() { [ControllerButton.RightTrigger] = ControllerButton.B },
        };

        var below = MappingEngine.Apply(new ControllerSnapshot(true, ControllerButton.None, RightTrigger: 30), profile);
        var above = MappingEngine.Apply(new ControllerSnapshot(true, ControllerButton.None, RightTrigger: 31), profile);

        Assert.False(below.Buttons.HasFlag(ControllerButton.B));
        Assert.True(above.Buttons.HasFlag(ControllerButton.B));
    }

    [Fact]
    public void Trigger_target_preserves_the_strongest_physical_intensity()
    {
        var input = new ControllerSnapshot(true, ControllerButton.None, LeftTrigger: 100, RightTrigger: 200);
        var profile = new MappingProfile
        {
            Id = "strongest-trigger",
            Name = "Strongest trigger",
            Bindings = new() { [ControllerButton.LeftTrigger] = ControllerButton.RightTrigger },
        };

        var output = MappingEngine.Apply(input, profile);

        Assert.Equal(200, output.RightTrigger);
    }

    [Fact]
    public void Digital_and_analog_sources_targeting_a_trigger_use_max_intensity()
    {
        var input = new ControllerSnapshot(true, ControllerButton.A, LeftTrigger: 180);
        var profile = new MappingProfile
        {
            Id = "mixed-trigger",
            Name = "Mixed trigger",
            Bindings = new()
            {
                [ControllerButton.A] = ControllerButton.RightTrigger,
                [ControllerButton.LeftTrigger] = ControllerButton.RightTrigger,
            },
        };

        var output = MappingEngine.Apply(input, profile);

        Assert.Equal(byte.MaxValue, output.RightTrigger);
    }

    [Theory]
    [InlineData(29, 31, true)]
    [InlineData(31, 29, true)]
    [InlineData(30, 30, false)]
    public void Trigger_collisions_targeting_a_button_are_order_independent(byte left, byte right, bool expected)
    {
        var input = new ControllerSnapshot(true, ControllerButton.None, LeftTrigger: left, RightTrigger: right);
        var profile = new MappingProfile
        {
            Id = "trigger-collision",
            Name = "Trigger collision",
            Bindings = new()
            {
                [ControllerButton.LeftTrigger] = ControllerButton.A,
                [ControllerButton.RightTrigger] = ControllerButton.A,
            },
        };

        var output = MappingEngine.Apply(input, profile);

        Assert.Equal(expected, output.Buttons.HasFlag(ControllerButton.A));
    }

    [Fact]
    public void Two_way_trigger_swap_uses_the_original_input_snapshot()
    {
        var input = new ControllerSnapshot(true, ControllerButton.None, LeftTrigger: 80, RightTrigger: 170);
        var profile = new MappingProfile
        {
            Id = "trigger-swap",
            Name = "Trigger swap",
            Bindings = new()
            {
                [ControllerButton.LeftTrigger] = ControllerButton.RightTrigger,
                [ControllerButton.RightTrigger] = ControllerButton.LeftTrigger,
            },
        };

        var output = MappingEngine.Apply(input, profile);

        Assert.Equal(170, output.LeftTrigger);
        Assert.Equal(80, output.RightTrigger);
    }
}
