using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class VirtualRemappingSafetyLatchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ally-bindings-latch-{Guid.NewGuid():N}");

    [Fact]
    public void Set_survives_new_instance_and_explicit_clear_removes_it()
    {
        var path = Path.Combine(_directory, "virtual-remapping-disabled");
        var latch = new VirtualRemappingSafetyLatch(path);

        Assert.False(latch.IsSet);
        Assert.True(latch.TrySet("controller recovery"));
        Assert.True(new VirtualRemappingSafetyLatch(path).IsSet);
        Assert.Contains("controller recovery", File.ReadAllText(path), StringComparison.Ordinal);

        latch.Clear();

        Assert.False(latch.IsSet);
    }

    [Fact]
    public void Repeated_set_atomically_replaces_the_reason_without_leaving_temporary_files()
    {
        var path = Path.Combine(_directory, "virtual-remapping-disabled");
        var latch = new VirtualRemappingSafetyLatch(path);

        Assert.True(latch.TrySet("first"));
        Assert.True(latch.TrySet("second"));

        var content = File.ReadAllText(path);
        Assert.DoesNotContain("first", content, StringComparison.Ordinal);
        Assert.Contains("second", content, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Write_failure_returns_false_and_leaves_no_false_positive_latch()
    {
        Directory.CreateDirectory(_directory);
        var directoryAsLatch = new VirtualRemappingSafetyLatch(_directory);

        Assert.False(directoryAsLatch.TrySet("cannot replace a directory"));
        Assert.False(directoryAsLatch.IsSet);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
