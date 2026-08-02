namespace AllyBindings.Core;

/// <summary>
/// Serializable, metadata-only schema-discovery contract. These DTOs deliberately
/// contain no payload containers, payload strings, hashes, timestamps or identifiers
/// derived from process/device instances.
/// </summary>
public sealed record UsbEtwSchemaDiscoveryReport(
    bool DiagnosticOnly,
    bool ContainsPayloadBytes,
    bool Complete,
    IReadOnlyList<UsbEtwSchemaShape> SchemaShapes,
    IReadOnlyList<UsbEtwMarkerShape> MarkerShapes);

public sealed record UsbEtwSchemaShape(
    int Phase,
    string ProviderName,
    string EventName,
    int EventId,
    int EventVersion,
    int Opcode,
    string PayloadPropertyCountBucket,
    int FieldOrdinal,
    string FieldName,
    string RuntimeType,
    string FieldLengthBucket,
    string TotalBinaryLengthBucket,
    long Count);

public sealed record UsbEtwMarkerShape(
    int Phase,
    string ProviderName,
    string EventName,
    int EventId,
    int EventVersion,
    int Opcode,
    string Kind,
    int StartFieldOrdinal,
    int EndFieldOrdinal,
    string StartFieldName,
    string EndFieldName,
    string StartRuntimeType,
    string EndRuntimeType,
    string StartLengthBucket,
    string EndLengthBucket,
    string StartOffsetBucket,
    string BytesAfterMarkerBucket,
    long Count);
