using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class RealtimeControllerPipelineTests
{
    private static readonly MappingProfile Profile = new()
    {
        Id = "game",
        Name = "Game",
        Bindings = new()
        {
            [ControllerButton.M1] = ControllerButton.A,
            [ControllerButton.M2] = ControllerButton.RightTrigger,
            [ControllerButton.X] = ControllerButton.Y,
        },
    };

    [Fact]
    public void Pipeline_mirrors_complete_physical_state_and_overlays_profiled_paddles()
    {
        var pipeline = new RealtimeControllerPipeline();
        pipeline.SetProfile(Profile);
        pipeline.ProcessPhysical(new ControllerSnapshot(
            true,
            ControllerButton.X | ControllerButton.LeftBumper,
            LeftTrigger: 73,
            LeftThumbX: 123,
            RightThumbY: -456));

        var output = pipeline.SetRearPaddle(ControllerButton.M1, true);

        Assert.Equal(ControllerButton.Y | ControllerButton.LeftBumper | ControllerButton.A, output.Buttons);
        Assert.Equal(73, output.LeftTrigger);
        Assert.Equal(123, output.LeftThumbX);
        Assert.Equal(-456, output.RightThumbY);
    }

    [Fact]
    public void Profile_change_rerenders_held_paddle_without_waiting_for_another_physical_packet()
    {
        var pipeline = new RealtimeControllerPipeline();
        pipeline.ProcessPhysical(new ControllerSnapshot(true, ControllerButton.None));
        pipeline.SetRearPaddle(ControllerButton.M1, true);

        var output = pipeline.SetProfile(Profile);

        Assert.True(output.Buttons.HasFlag(ControllerButton.A));
    }

    [Fact]
    public void Disconnect_releases_paddles_and_returns_a_neutral_disconnected_snapshot()
    {
        var pipeline = new RealtimeControllerPipeline();
        pipeline.SetProfile(Profile);
        pipeline.ProcessPhysical(new ControllerSnapshot(true, ControllerButton.None));
        pipeline.SetRearPaddle(ControllerButton.M2, true);

        Assert.Equal(ControllerSnapshot.Disconnected, pipeline.ProcessPhysical(ControllerSnapshot.Disconnected));
        Assert.Equal(ControllerSnapshot.Disconnected, pipeline.ProcessPhysical(new ControllerSnapshot(true, ControllerButton.None)) with { IsConnected = false });
    }

    [Fact]
    public void Reset_clears_profile_paddles_and_physical_state()
    {
        var pipeline = new RealtimeControllerPipeline();
        pipeline.SetProfile(Profile);
        pipeline.ProcessPhysical(new ControllerSnapshot(true, ControllerButton.X));
        pipeline.SetRearPaddle(ControllerButton.M1, true);

        Assert.Equal(ControllerSnapshot.Disconnected, pipeline.Reset());
        Assert.Equal(MappingProfile.Default.Id, pipeline.ActiveProfileId);
    }

    [Fact]
    public void Rejects_non_paddle_key_state_updates()
    {
        var pipeline = new RealtimeControllerPipeline();
        Assert.Throws<ArgumentOutOfRangeException>(() => pipeline.SetRearPaddle(ControllerButton.A, true));
    }
}
