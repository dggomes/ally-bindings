namespace AllyBindings.Core;

public enum UsbEtwMarkerKind
{
    FullMarkerSingleField,
    CommandMarkerSingleField,
    FullMarkerSplitAdjacentFields,
    CommandMarkerSplitAdjacentFields,
}

public sealed record UsbEtwDiscoveryField(
    int Ordinal,
    string Name,
    string RuntimeType,
    int ObservedLength,
    byte[] MarkerComparableBytes);

public sealed record UsbEtwMarkerObservation(
    UsbEtwMarkerKind Kind,
    int StartFieldOrdinal,
    int EndFieldOrdinal,
    int StartOffset,
    int BytesAvailableAfterMarker);

/// <summary>
/// Inspects decoded ETW properties in memory for allowlisted ASUS command markers.
/// Results describe only framing metadata; payload bytes are never returned.
/// </summary>
public static class UsbEtwSchemaDiscovery
{
    private static readonly byte[] FullMarker = [0x5A, 0xD1, 0x02, 0x08, 0x2C];
    private static readonly byte[] CommandMarker = [0xD1, 0x02, 0x08, 0x2C];

    public static IReadOnlyList<UsbEtwMarkerObservation> Inspect(
        IReadOnlyList<UsbEtwDiscoveryField> orderedFields,
        int maximumObservations = 64)
    {
        ArgumentNullException.ThrowIfNull(orderedFields);
        if (maximumObservations <= 0) throw new ArgumentOutOfRangeException(nameof(maximumObservations));

        var observations = new List<UsbEtwMarkerObservation>();
        for (var fieldIndex = 0; fieldIndex < orderedFields.Count; fieldIndex++)
        {
            var field = orderedFields[fieldIndex];
            AddSingleFieldObservations(field, FullMarker, UsbEtwMarkerKind.FullMarkerSingleField, observations, maximumObservations);
            AddSingleFieldObservations(
                field,
                CommandMarker,
                UsbEtwMarkerKind.CommandMarkerSingleField,
                observations,
                maximumObservations,
                suppressWhenPrecededByReportId: true);

            if (fieldIndex == 0) continue;
            var previous = orderedFields[fieldIndex - 1];
            AddAdjacentFieldObservations(
                previous,
                field,
                FullMarker,
                UsbEtwMarkerKind.FullMarkerSplitAdjacentFields,
                observations,
                maximumObservations);
            AddAdjacentFieldObservations(
                previous,
                field,
                CommandMarker,
                UsbEtwMarkerKind.CommandMarkerSplitAdjacentFields,
                observations,
                maximumObservations);
        }
        return observations;
    }

    private static void AddSingleFieldObservations(
        UsbEtwDiscoveryField field,
        byte[] marker,
        UsbEtwMarkerKind kind,
        List<UsbEtwMarkerObservation> observations,
        int maximumObservations,
        bool suppressWhenPrecededByReportId = false)
    {
        var bytes = field.MarkerComparableBytes;
        for (var offset = 0; offset <= bytes.Length - marker.Length; offset++)
        {
            if (!bytes.AsSpan(offset, marker.Length).SequenceEqual(marker)) continue;
            if (suppressWhenPrecededByReportId && offset > 0 && bytes[offset - 1] == FullMarker[0]) continue;
            AddObservation(
                observations,
                maximumObservations,
                new(
                    kind,
                    field.Ordinal,
                    field.Ordinal,
                    offset,
                    bytes.Length - offset - marker.Length));
        }
    }

    private static void AddAdjacentFieldObservations(
        UsbEtwDiscoveryField first,
        UsbEtwDiscoveryField second,
        byte[] marker,
        UsbEtwMarkerKind kind,
        List<UsbEtwMarkerObservation> observations,
        int maximumObservations)
    {
        if (second.Ordinal != first.Ordinal + 1) return;
        var firstBytes = first.MarkerComparableBytes;
        var secondBytes = second.MarkerComparableBytes;
        for (var firstPartLength = 1; firstPartLength < marker.Length; firstPartLength++)
        {
            var secondPartLength = marker.Length - firstPartLength;
            if (firstBytes.Length < firstPartLength || secondBytes.Length < secondPartLength) continue;
            var startOffset = firstBytes.Length - firstPartLength;
            if (!firstBytes.AsSpan(startOffset, firstPartLength).SequenceEqual(marker.AsSpan(0, firstPartLength)) ||
                !secondBytes.AsSpan(0, secondPartLength).SequenceEqual(marker.AsSpan(firstPartLength)))
            {
                continue;
            }
            AddObservation(
                observations,
                maximumObservations,
                new(
                    kind,
                    first.Ordinal,
                    second.Ordinal,
                    startOffset,
                    secondBytes.Length - secondPartLength));
        }
    }

    private static void AddObservation(
        List<UsbEtwMarkerObservation> observations,
        int maximumObservations,
        UsbEtwMarkerObservation observation)
    {
        if (observations.Count == maximumObservations)
        {
            throw new UsbEtwSchemaDiscoveryLimitException();
        }
        observations.Add(observation);
    }
}

public sealed class UsbEtwSchemaDiscoveryLimitException : Exception
{
    public UsbEtwSchemaDiscoveryLimitException()
        : base("The bounded USB ETW schema-discovery observation limit was exceeded.")
    {
    }
}
