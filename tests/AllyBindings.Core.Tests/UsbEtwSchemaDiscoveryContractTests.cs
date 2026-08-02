using System.Reflection;
using System.Text.Json;
using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class UsbEtwSchemaDiscoveryContractTests
{
    [Fact]
    public void Serializable_contract_has_only_explicit_metadata_properties()
    {
        AssertContract<UsbEtwSchemaDiscoveryReport>(
            nameof(UsbEtwSchemaDiscoveryReport.DiagnosticOnly),
            nameof(UsbEtwSchemaDiscoveryReport.ContainsPayloadBytes),
            nameof(UsbEtwSchemaDiscoveryReport.Complete),
            nameof(UsbEtwSchemaDiscoveryReport.SchemaShapes),
            nameof(UsbEtwSchemaDiscoveryReport.MarkerShapes));
        AssertContract<UsbEtwSchemaShape>(
            nameof(UsbEtwSchemaShape.Phase),
            nameof(UsbEtwSchemaShape.ProviderName),
            nameof(UsbEtwSchemaShape.EventName),
            nameof(UsbEtwSchemaShape.EventId),
            nameof(UsbEtwSchemaShape.EventVersion),
            nameof(UsbEtwSchemaShape.Opcode),
            nameof(UsbEtwSchemaShape.PayloadPropertyCountBucket),
            nameof(UsbEtwSchemaShape.FieldOrdinal),
            nameof(UsbEtwSchemaShape.FieldName),
            nameof(UsbEtwSchemaShape.RuntimeType),
            nameof(UsbEtwSchemaShape.FieldLengthBucket),
            nameof(UsbEtwSchemaShape.TotalBinaryLengthBucket),
            nameof(UsbEtwSchemaShape.Count));
        AssertContract<UsbEtwMarkerShape>(
            nameof(UsbEtwMarkerShape.Phase),
            nameof(UsbEtwMarkerShape.ProviderName),
            nameof(UsbEtwMarkerShape.EventName),
            nameof(UsbEtwMarkerShape.EventId),
            nameof(UsbEtwMarkerShape.EventVersion),
            nameof(UsbEtwMarkerShape.Opcode),
            nameof(UsbEtwMarkerShape.Kind),
            nameof(UsbEtwMarkerShape.StartFieldOrdinal),
            nameof(UsbEtwMarkerShape.EndFieldOrdinal),
            nameof(UsbEtwMarkerShape.StartFieldName),
            nameof(UsbEtwMarkerShape.EndFieldName),
            nameof(UsbEtwMarkerShape.StartRuntimeType),
            nameof(UsbEtwMarkerShape.EndRuntimeType),
            nameof(UsbEtwMarkerShape.StartLengthBucket),
            nameof(UsbEtwMarkerShape.EndLengthBucket),
            nameof(UsbEtwMarkerShape.StartOffsetBucket),
            nameof(UsbEtwMarkerShape.BytesAfterMarkerBucket),
            nameof(UsbEtwMarkerShape.Count));

        AssertSerializationGraphSafe(
            typeof(UsbEtwSchemaDiscoveryReport),
            new HashSet<Type>(),
            nameof(UsbEtwSchemaDiscoveryReport));
    }

    [Fact]
    public void Serialized_contract_cannot_include_inspected_payload_sentinel()
    {
        byte[] inspectedPayload = [0xD1, 0x02, 0x08, 0x2C, 0xDE, 0xAD, 0xBE, 0xEF];
        var observation = Assert.Single(UsbEtwSchemaDiscovery.Inspect(
            [new UsbEtwDiscoveryField(0, "field-0", "ByteArray", inspectedPayload.Length, inspectedPayload)]));
        var report = new UsbEtwSchemaDiscoveryReport(
            DiagnosticOnly: true,
            ContainsPayloadBytes: false,
            Complete: true,
            SchemaShapes: [],
            MarkerShapes:
            [
                new(
                    1,
                    "provider",
                    "event",
                    1,
                    0,
                    0,
                    observation.Kind.ToString(),
                    observation.StartFieldOrdinal,
                    observation.EndFieldOrdinal,
                    "field-0",
                    "field-0",
                    "ByteArray",
                    "ByteArray",
                    "8",
                    "8",
                    observation.StartOffset.ToString(),
                    observation.BytesAvailableAfterMarker.ToString(),
                    1),
            ]);

        var json = JsonSerializer.Serialize(report);

        Assert.DoesNotContain("deadbeef", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(inspectedPayload), json, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(inspectedPayload), json, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertContract<T>(params string[] expectedProperties)
    {
        var actual = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedProperties.Order(StringComparer.Ordinal), actual);
    }

    private static bool IsByteContainer(Type type)
    {
        if (type == typeof(byte) || type == typeof(byte[]) ||
            type == typeof(Memory<byte>) || type == typeof(ReadOnlyMemory<byte>)) return true;
        if (type.IsArray) return IsByteContainer(type.GetElementType()!);
        return type.IsGenericType && type.GetGenericArguments().Any(IsByteContainer);
    }

    private static void AssertSerializationGraphSafe(Type type, HashSet<Type> visited, string path)
    {
        Assert.False(IsByteContainer(type), $"Byte container reachable at {path}: {type}.");

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                AssertSerializationGraphSafe(argument, visited, $"{path}<{argument.Name}>");
            }
        }
        if (type.IsArray)
        {
            AssertSerializationGraphSafe(type.GetElementType()!, visited, $"{path}[]");
            return;
        }
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
            type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true || !visited.Add(type))
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var propertyPath = $"{path}.{property.Name}";
            Assert.False(
                property.Name.Contains("Hash", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Digest", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Hex", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Base64", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Timestamp", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Qpc", StringComparison.OrdinalIgnoreCase),
                $"Forbidden serialization property reachable at {propertyPath}.");
            AssertSerializationGraphSafe(property.PropertyType, visited, propertyPath);
        }
    }
}
