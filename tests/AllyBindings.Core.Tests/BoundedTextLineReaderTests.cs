using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class BoundedTextLineReaderTests
{
    [Fact]
    public async Task Preserves_coalesced_frames_across_reads()
    {
        using var source = new StringReader("first\nsecond\r\nthird\n");
        using var reader = new BoundedTextLineReader(source);

        Assert.Equal("first", await reader.ReadLineAsync(16, CancellationToken.None));
        Assert.Equal("second", await reader.ReadLineAsync(16, CancellationToken.None));
        Assert.Equal("third", await reader.ReadLineAsync(16, CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(16, CancellationToken.None));
    }

    [Fact]
    public async Task Preserves_partial_frame_at_end_of_stream()
    {
        using var source = new StringReader("partial");
        using var reader = new BoundedTextLineReader(source);

        Assert.Equal("partial", await reader.ReadLineAsync(16, CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(16, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_oversized_frame_and_fails_closed_afterward()
    {
        using var source = new StringReader("12345\nnext\n");
        using var reader = new BoundedTextLineReader(source);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => reader.ReadLineAsync(4, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadLineAsync(4, CancellationToken.None));
    }
}
