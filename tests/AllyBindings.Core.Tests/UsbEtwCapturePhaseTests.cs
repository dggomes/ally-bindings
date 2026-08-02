using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class UsbEtwCapturePhaseTests
{
    [Theory]
    [InlineData(1, 123456789L)]
    [InlineData(2, 987654321L)]
    [InlineData(3, long.MaxValue)]
    public void Phase_commands_round_trip(int phase, long boundaryQpc)
    {
        var command = UsbEtwCapturePhaseCommand.Format(phase, boundaryQpc);

        Assert.True(UsbEtwCapturePhaseCommand.TryParse(command, out var parsedPhase, out var parsedBoundary));
        Assert.Equal(phase, parsedPhase);
        Assert.Equal(boundaryQpc, parsedBoundary);
        Assert.InRange(command.Length, 1, UsbEtwCapturePhaseCommand.MaximumCommandCharacters);
    }

    [Theory]
    [InlineData("")]
    [InlineData("stage-0:1")]
    [InlineData("stage-4:1")]
    [InlineData("stage-1:0")]
    [InlineData("stage-1:-1")]
    [InlineData("stage-1:+1")]
    [InlineData("stage-01:1")]
    [InlineData("stage-1 1")]
    [InlineData("stage-1:1:2")]
    [InlineData("stop:1")]
    public void Phase_command_parser_rejects_noncanonical_input(string command)
    {
        Assert.False(UsbEtwCapturePhaseCommand.TryParse(command, out _, out _));
    }

    [Fact]
    public void Buffered_events_are_classified_by_event_qpc_not_transition_processing_time()
    {
        var phases = new UsbEtwCapturePhaseBoundaries();
        phases.Record(1, 1_000);
        phases.Record(2, 2_000);
        phases.Record(3, 3_000);

        Assert.Equal(0, phases.Classify(999));
        Assert.Equal(1, phases.Classify(1_000));
        Assert.Equal(1, phases.Classify(1_999));
        Assert.Equal(2, phases.Classify(2_000));
        Assert.Equal(2, phases.Classify(2_999));
        Assert.Equal(3, phases.Classify(3_000));
        Assert.Equal(3, phases.Classify(4_000));
    }

    [Fact]
    public void Phase_boundaries_must_be_ordered_and_monotonic()
    {
        var phases = new UsbEtwCapturePhaseBoundaries();

        Assert.Throws<InvalidDataException>(() => phases.Record(2, 2_000));
        phases.Record(1, 1_000);
        Assert.Throws<InvalidDataException>(() => phases.Record(1, 1_001));
        Assert.Throws<InvalidDataException>(() => phases.Record(2, 999));
        phases.Record(2, 1_000);
        phases.Record(3, 1_000);
    }
}
