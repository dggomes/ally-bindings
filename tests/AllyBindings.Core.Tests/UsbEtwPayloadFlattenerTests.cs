using System.Collections;
using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class UsbEtwPayloadFlattenerTests
{
    [Fact]
    public void Flattens_dictionary_backed_traceevent_structures_in_order()
    {
        var transfer = new Dictionary<string, object>
        {
            ["SetupPacket"] = new Dictionary<string, object>
            {
                ["RequestType"] = (byte)0x21,
                ["Request"] = (byte)0x09,
            },
            ["TransferBuffer"] = new byte[] { 0x5A, 0xD1, 0x02, 0x08, 0x2C },
        };

        var result = UsbEtwPayloadFlattener.Flatten(
            [
                new("fid_UcxController", 123UL),
                new("fid_UCX_URB_CONTROL_TRANSFER", transfer),
            ],
            maximumFields: 32,
            maximumDepth: 4);

        Assert.False(result.LimitExceeded);
        Assert.Equal(
            [
                "fid_UcxController",
                "fid_UCX_URB_CONTROL_TRANSFER.SetupPacket.RequestType",
                "fid_UCX_URB_CONTROL_TRANSFER.SetupPacket.Request",
                "fid_UCX_URB_CONTROL_TRANSFER.TransferBuffer",
            ],
            result.Fields.Select(field => field.Name));
        Assert.Equal(
            new byte[] { 0x5A, 0xD1, 0x02, 0x08, 0x2C },
            Assert.IsType<byte[]>(result.Fields[^1].Value));
    }

    [Fact]
    public void Flattens_arrays_of_nested_structures_but_keeps_byte_arrays_as_leaves()
    {
        var result = UsbEtwPayloadFlattener.Flatten(
            [
                new(
                    "Transfers",
                    new object[]
                    {
                        new Dictionary<string, object> { ["Buffer"] = new byte[] { 1, 2, 3 } },
                        new Dictionary<string, object> { ["Status"] = 0 },
                    }),
            ],
            maximumFields: 8,
            maximumDepth: 4);

        Assert.False(result.LimitExceeded);
        Assert.Equal(
            ["Transfers[0].Buffer", "Transfers[1].Status"],
            result.Fields.Select(field => field.Name));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(result.Fields[0].Value));
    }

    [Fact]
    public void Nested_transfer_buffer_reaches_metadata_only_marker_inspection()
    {
        var result = UsbEtwPayloadFlattener.Flatten(
            [
                new(
                    "fid_UCX_URB_CONTROL_TRANSFER",
                    new Dictionary<string, object>
                    {
                        ["TransferBuffer"] = new byte[] { 0x5A, 0xD1, 0x02, 0x08, 0x2C, 0x00 },
                    }),
            ],
            maximumFields: 8,
            maximumDepth: 4);
        var discoveryFields = result.Fields.Select((field, ordinal) =>
        {
            var bytes = field.Value as byte[] ?? [];
            return new UsbEtwDiscoveryField(
                ordinal,
                field.Name,
                field.Value?.GetType().Name ?? "null",
                bytes.Length,
                bytes);
        }).ToArray();

        var marker = Assert.Single(UsbEtwSchemaDiscovery.Inspect(discoveryFields));
        Assert.Equal(UsbEtwMarkerKind.FullMarkerSingleField, marker.Kind);
        Assert.Equal(0, marker.StartFieldOrdinal);
        Assert.Equal("fid_UCX_URB_CONTROL_TRANSFER.TransferBuffer", discoveryFields[marker.StartFieldOrdinal].Name);
    }

    [Fact]
    public void Fails_closed_when_the_leaf_limit_is_exceeded()
    {
        var result = UsbEtwPayloadFlattener.Flatten(
            [new("Struct", new Dictionary<string, object> { ["A"] = 1, ["B"] = 2, ["C"] = 3 })],
            maximumFields: 2,
            maximumDepth: 4);

        Assert.True(result.LimitExceeded);
        Assert.Equal(2, result.Fields.Count);
    }

    [Fact]
    public void Stops_enumerating_a_container_as_soon_as_the_leaf_limit_is_known()
    {
        var structure = new CountingStructure(1_000);

        var result = UsbEtwPayloadFlattener.Flatten(
            [new("Struct", structure)],
            maximumFields: 2,
            maximumDepth: 4);

        Assert.True(result.LimitExceeded);
        Assert.Equal(2, result.Fields.Count);
        Assert.Equal(3, structure.YieldedCount);
    }

    [Fact]
    public void Fails_closed_on_total_node_limit_and_multidimensional_arrays()
    {
        var emptyContainers = Enumerable.Range(0, 20)
            .Select(_ => (object)new Dictionary<string, object>())
            .ToArray();
        var nodeLimited = UsbEtwPayloadFlattener.Flatten(
            [new("Containers", emptyContainers)],
            maximumFields: 32,
            maximumDepth: 4,
            maximumVisitedNodes: 8);

        Assert.True(nodeLimited.LimitExceeded);

        var multidimensional = UsbEtwPayloadFlattener.Flatten(
            [new("Matrix", new int[2, 2])],
            maximumFields: 32,
            maximumDepth: 4);

        Assert.True(multidimensional.LimitExceeded);
        Assert.Empty(multidimensional.Fields);
    }

    [Fact]
    public void Fails_closed_when_depth_or_cycles_exceed_the_bound()
    {
        var tooDeep = UsbEtwPayloadFlattener.Flatten(
            [
                new(
                    "Struct",
                    new Dictionary<string, object>
                    {
                        ["Nested"] = new Dictionary<string, object> { ["Leaf"] = 1 },
                    }),
            ],
            maximumFields: 8,
            maximumDepth: 1);
        Assert.True(tooDeep.LimitExceeded);

        var cyclic = new Dictionary<string, object>();
        cyclic["Self"] = cyclic;
        var cycleResult = UsbEtwPayloadFlattener.Flatten(
            [new("Struct", cyclic)],
            maximumFields: 8,
            maximumDepth: 4);
        Assert.True(cycleResult.LimitExceeded);
        Assert.Empty(cycleResult.Fields);
    }

    [Fact]
    public void Bounds_untrusted_metadata_paths()
    {
        var longName = new string('x', 400);
        var result = UsbEtwPayloadFlattener.Flatten(
            [new(longName, new Dictionary<string, object> { [longName] = 1 })],
            maximumFields: 8,
            maximumDepth: 4);

        Assert.False(result.LimitExceeded);
        Assert.Single(result.Fields);
        Assert.True(result.Fields[0].Name.Length <= 256);
    }

    private sealed class CountingStructure(int count) : IEnumerable<KeyValuePair<string, object>>
    {
        public int YieldedCount { get; private set; }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            for (var index = 0; index < count; index++)
            {
                YieldedCount++;
                yield return new($"Field{index}", index);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
