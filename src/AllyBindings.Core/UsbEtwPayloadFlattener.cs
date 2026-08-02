using System.Collections;

namespace AllyBindings.Core;

internal sealed record UsbEtwFlattenedPayloadField(string Name, object? Value);

internal sealed record UsbEtwPayloadFlatteningResult(
    IReadOnlyList<UsbEtwFlattenedPayloadField> Fields,
    bool LimitExceeded);

/// <summary>
/// Flattens TraceEvent's dictionary-backed nested structure values in memory so
/// bounded HID marker extraction can inspect byte-array leaves. The flattened
/// values are transient and are never part of the serialized discovery contract.
/// </summary>
internal static class UsbEtwPayloadFlattener
{
    private const int MaximumPathCharacters = 256;
    private const int MaximumSegmentCharacters = 64;

    public static UsbEtwPayloadFlatteningResult Flatten(
        IReadOnlyList<KeyValuePair<string, object?>> topLevelFields,
        int maximumFields,
        int maximumDepth,
        int maximumVisitedNodes = 1_024)
    {
        ArgumentNullException.ThrowIfNull(topLevelFields);
        if (maximumFields <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFields));
        if (maximumDepth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        if (maximumVisitedNodes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumVisitedNodes));

        var fields = new List<UsbEtwFlattenedPayloadField>(Math.Min(topLevelFields.Count, maximumFields));
        var activeContainers = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var limitExceeded = false;
        var visitedNodes = 0;

        foreach (var field in topLevelFields)
        {
            Visit(BoundSegment(field.Key), field.Value, depth: 0);
            if (limitExceeded) break;
        }

        return new(fields, limitExceeded);

        void Visit(string path, object? value, int depth)
        {
            visitedNodes++;
            if (visitedNodes > maximumVisitedNodes)
            {
                limitExceeded = true;
                return;
            }
            if (fields.Count == maximumFields)
            {
                limitExceeded = true;
                return;
            }

            if (value is byte[] or ArraySegment<byte> or ReadOnlyMemory<byte> or Memory<byte>)
            {
                fields.Add(new(path, value));
                return;
            }

            if (value is IEnumerable<KeyValuePair<string, object>> structure)
            {
                if (depth == maximumDepth || !activeContainers.Add(value))
                {
                    limitExceeded = true;
                    return;
                }
                try
                {
                    foreach (var child in structure)
                    {
                        Visit(AppendPath(path, child.Key), child.Value, depth + 1);
                        if (limitExceeded) break;
                    }
                }
                finally
                {
                    activeContainers.Remove(value);
                }
                return;
            }

            if (value is Array array)
            {
                if (array.Rank != 1 || depth == maximumDepth || !activeContainers.Add(value))
                {
                    limitExceeded = true;
                    return;
                }
                try
                {
                    for (var index = 0; index < array.Length; index++)
                    {
                        Visit(AppendPath(path, index), array.GetValue(index), depth + 1);
                        if (limitExceeded) break;
                    }
                }
                finally
                {
                    activeContainers.Remove(value);
                }
                return;
            }

            fields.Add(new(path, value));
        }
    }

    private static string AppendPath(string parent, string child)
    {
        var path = $"{parent}.{BoundSegment(child)}";
        return path.Length <= MaximumPathCharacters ? path : path[..MaximumPathCharacters];
    }

    private static string AppendPath(string parent, int index)
    {
        var path = $"{parent}[{index}]";
        return path.Length <= MaximumPathCharacters ? path : path[..MaximumPathCharacters];
    }

    private static string BoundSegment(string? segment)
    {
        var value = string.IsNullOrWhiteSpace(segment) ? "field" : segment;
        return value.Length <= MaximumSegmentCharacters ? value : value[..MaximumSegmentCharacters];
    }
}
