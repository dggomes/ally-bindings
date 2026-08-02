using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class UsbEtwPrioritizedSchemaCounterTests
{
    [Fact]
    public void Framing_noise_cannot_starve_later_priority_transfer_metadata()
    {
        var counter = new UsbEtwPrioritizedSchemaCounter<Key>(
            key => key.Phase,
            maximumPriorityKeys: 4,
            maximumPriorityKeysPerPhase: 1,
            maximumFramingKeys: 4,
            maximumFramingKeysPerPhase: 2);

        Assert.True(counter.Increment(new(1, "framing-a"), UsbEtwSchemaRetentionClass.Framing));
        Assert.True(counter.Increment(new(1, "framing-b"), UsbEtwSchemaRetentionClass.Framing));
        Assert.False(counter.Increment(new(1, "framing-overflow"), UsbEtwSchemaRetentionClass.Framing));

        Assert.True(counter.Increment(new(1, "fid_URB_TransferData"), UsbEtwSchemaRetentionClass.Priority));
        Assert.Contains(
            counter.Entries,
            pair => pair.Key.Name == "fid_URB_TransferData" && pair.Value == 1);
    }

    [Fact]
    public void Repeated_shape_saturates_count_without_consuming_another_slot()
    {
        var counter = new UsbEtwPrioritizedSchemaCounter<Key>(
            key => key.Phase,
            maximumPriorityKeys: 1,
            maximumPriorityKeysPerPhase: 1,
            maximumFramingKeys: 1,
            maximumFramingKeysPerPhase: 1);
        var key = new Key(1, "fid_IRP_NtStatus");

        Assert.True(counter.Increment(key, UsbEtwSchemaRetentionClass.Priority));
        Assert.True(counter.Increment(key, UsbEtwSchemaRetentionClass.Priority));

        Assert.Equal(2, Assert.Single(counter.Entries).Value);
    }

    private sealed record Key(int Phase, string Name);
}
