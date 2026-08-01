using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class PreviewControllerBackendTests
{
    [Fact]
    public async Task Apply_is_truthful_about_preview_only_state()
    {
        await using var backend = new PreviewControllerBackend();
        var result = await backend.ApplyAsync(new MappingProfile { Id = "test", Name = "Test" });

        Assert.False(result.CommandAccepted);
        Assert.False(result.Status.CanRemap);
        Assert.True(result.Status.PhysicalPassthroughIntact);
        Assert.Contains("preview mode", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
