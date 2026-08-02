using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class UsbEtwCapturePhaseTests
{
    [Theory]
    [InlineData(1, UsbEtwCapturePhaseTransition.Start, "start-1")]
    [InlineData(1, UsbEtwCapturePhaseTransition.End, "end-1")]
    [InlineData(2, UsbEtwCapturePhaseTransition.Start, "start-2")]
    [InlineData(2, UsbEtwCapturePhaseTransition.End, "end-2")]
    [InlineData(3, UsbEtwCapturePhaseTransition.Start, "start-3")]
    [InlineData(3, UsbEtwCapturePhaseTransition.End, "end-3")]
    public void Phase_command_round_trips(
        int phase,
        UsbEtwCapturePhaseTransition transition,
        string expected)
    {
        var command = UsbEtwCapturePhaseCommand.Format(phase, transition);

        Assert.Equal(expected, command);
        Assert.True(UsbEtwCapturePhaseCommand.TryParse(command, out var parsedPhase, out var parsedTransition));
        Assert.Equal(phase, parsedPhase);
        Assert.Equal(transition, parsedTransition);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stage-1")]
    [InlineData("start-0")]
    [InlineData("start-4")]
    [InlineData("start-01")]
    [InlineData("start-1 ")]
    [InlineData("START-1")]
    [InlineData("end-1:123")]
    [InlineData("start-1-extra")]
    [InlineData("start-1111111111111111")]
    public void Phase_command_rejects_noncanonical_input(string? command)
    {
        Assert.False(UsbEtwCapturePhaseCommand.TryParse(command, out _, out _));
    }

    [Fact]
    public void Closed_windows_classify_boundaries_and_idle_traffic()
    {
        var windows = new UsbEtwCapturePhaseWindows();
        windows.StartAt(1, 100);
        windows.EndAt(1, 200);
        windows.StartAt(2, 300);
        windows.EndAt(2, 400);
        windows.StartAt(3, 500);
        windows.EndAt(3, 600);

        Assert.Equal(0, windows.Classify(99));
        Assert.Equal(1, windows.Classify(100));
        Assert.Equal(1, windows.Classify(199));
        Assert.Equal(0, windows.Classify(200));
        Assert.Equal(0, windows.Classify(299));
        Assert.Equal(2, windows.Classify(300));
        Assert.Equal(2, windows.Classify(399));
        Assert.Equal(0, windows.Classify(400));
        Assert.Equal(3, windows.Classify(500));
        Assert.Equal(3, windows.Classify(599));
        Assert.Equal(0, windows.Classify(600));
    }

    [Fact]
    public void Buffered_events_are_classified_by_occurrence_not_processing_order()
    {
        var windows = new UsbEtwCapturePhaseWindows();
        windows.StartAt(1, 1_000);
        windows.EndAt(1, 2_000);
        windows.StartAt(2, 3_000);

        Assert.Equal(0, windows.Classify(999));
        Assert.Equal(1, windows.Classify(1_500));
        Assert.Equal(0, windows.Classify(2_500));
        Assert.Equal(2, windows.Classify(3_500));
    }

    [Fact]
    public void Windows_reject_overlap_out_of_order_and_nonmonotonic_transitions()
    {
        var windows = new UsbEtwCapturePhaseWindows();

        Assert.Throws<InvalidOperationException>(() => windows.StartAt(2, 100));
        windows.StartAt(1, 100);
        Assert.Throws<InvalidOperationException>(() => windows.StartAt(2, 110));
        Assert.Throws<InvalidOperationException>(() => windows.EndAt(2, 120));
        Assert.Throws<InvalidOperationException>(() => windows.EndAt(1, 99));
        windows.EndAt(1, 200);
        Assert.Throws<InvalidOperationException>(() => windows.StartAt(2, 199));
    }

    [Fact]
    public void Live_boundaries_are_sampled_inside_the_serialized_transition()
    {
        var windows = new UsbEtwCapturePhaseWindows();

        var start = windows.StartNow(1);
        Assert.Equal(1, windows.Classify(start));
        var end = windows.EndNow(1);

        Assert.True(end >= start);
        Assert.Equal(1, windows.Classify(end - 1));
        Assert.Equal(0, windows.Classify(end));
    }
}
