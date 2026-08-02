using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class UsbEtwSchemaDiscoveryTests
{
    [Fact]
    public void Finds_full_marker_without_returning_payload_bytes()
    {
        var fields = new[]
        {
            Field(0, [0x00, 0x5A, 0xD1, 0x02, 0x08, 0x2C, 0xAA]),
        };

        var observation = Assert.Single(UsbEtwSchemaDiscovery.Inspect(fields));

        Assert.Equal(UsbEtwMarkerKind.FullMarkerSingleField, observation.Kind);
        Assert.Equal(1, observation.StartOffset);
        Assert.Equal(1, observation.BytesAvailableAfterMarker);
        Assert.DoesNotContain(
            observation.GetType().GetProperties(),
            property => property.PropertyType == typeof(byte[]));
    }

    [Fact]
    public void Finds_command_marker_when_report_id_is_not_in_the_same_field()
    {
        var observation = Assert.Single(UsbEtwSchemaDiscovery.Inspect(
            [Field(0, [0xD1, 0x02, 0x08, 0x2C, 0xAA])]));

        Assert.Equal(UsbEtwMarkerKind.CommandMarkerSingleField, observation.Kind);
        Assert.Equal(1, observation.BytesAvailableAfterMarker);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Finds_full_marker_at_every_adjacent_field_boundary(int firstPartLength)
    {
        byte[] marker = [0x5A, 0xD1, 0x02, 0x08, 0x2C];
        var fields = new[]
        {
            Field(4, marker[..firstPartLength]),
            Field(5, [.. marker[firstPartLength..], 0xAA]),
        };

        var observation = Assert.Single(
            UsbEtwSchemaDiscovery.Inspect(fields),
            item => item.Kind == UsbEtwMarkerKind.FullMarkerSplitAdjacentFields);

        Assert.Equal(4, observation.StartFieldOrdinal);
        Assert.Equal(5, observation.EndFieldOrdinal);
        Assert.Equal(1, observation.BytesAvailableAfterMarker);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Finds_command_marker_at_every_adjacent_field_boundary(int firstPartLength)
    {
        byte[] marker = [0xD1, 0x02, 0x08, 0x2C];
        var fields = new[]
        {
            Field(4, marker[..firstPartLength]),
            Field(5, [.. marker[firstPartLength..], 0xAA]),
        };

        var observation = Assert.Single(UsbEtwSchemaDiscovery.Inspect(fields));

        Assert.Equal(UsbEtwMarkerKind.CommandMarkerSplitAdjacentFields, observation.Kind);
        Assert.Equal(4, observation.StartFieldOrdinal);
        Assert.Equal(5, observation.EndFieldOrdinal);
        Assert.Equal(1, observation.BytesAvailableAfterMarker);
    }

    [Fact]
    public void Finds_scalar_report_id_followed_by_binary_command_marker()
    {
        var fields = new[]
        {
            Field(0, [0x5A], "Byte"),
            Field(1, [0xD1, 0x02, 0x08, 0x2C, 0xAA], "ByteArray"),
        };

        var observations = UsbEtwSchemaDiscovery.Inspect(fields);

        Assert.Equal(2, observations.Count);
        Assert.Contains(observations, item => item.Kind == UsbEtwMarkerKind.FullMarkerSplitAdjacentFields);
        Assert.Contains(observations, item => item.Kind == UsbEtwMarkerKind.CommandMarkerSingleField);
    }

    [Fact]
    public void Does_not_match_short_partial_markers_or_nonadjacent_fields()
    {
        var fields = new[]
        {
            Field(0, [0x5A, 0xD1, 0x02]),
            Field(1, [0x99]),
            Field(2, [0x08, 0x2C]),
        };

        Assert.Empty(UsbEtwSchemaDiscovery.Inspect(fields));
    }

    [Fact]
    public void Does_not_join_byte_fields_across_a_non_byte_property()
    {
        var fields = new[]
        {
            Field(0, [0x5A, 0xD1]),
            Field(1, [], "UInt32"),
            Field(2, [0x02, 0x08, 0x2C]),
        };

        Assert.Empty(UsbEtwSchemaDiscovery.Inspect(fields));
    }

    [Fact]
    public void Fails_closed_when_observation_limit_is_exceeded()
    {
        var fields = new[]
        {
            Field(0, [0xD1, 0x02, 0x08, 0x2C, 0xD1, 0x02, 0x08, 0x2C]),
        };

        Assert.Throws<UsbEtwSchemaDiscoveryLimitException>(
            () => UsbEtwSchemaDiscovery.Inspect(fields, maximumObservations: 1));
    }

    private static UsbEtwDiscoveryField Field(int ordinal, byte[] bytes, string runtimeType = "ByteArray") =>
        new(ordinal, $"field-{ordinal}", runtimeType, bytes.Length, bytes);
}
