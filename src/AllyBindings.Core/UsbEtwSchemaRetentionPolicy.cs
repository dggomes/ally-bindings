namespace AllyBindings.Core;

internal enum UsbEtwSchemaRetentionClass
{
    None,
    Priority,
    Framing,
}

/// <summary>
/// Selects the metadata-only ETW property shapes worth retaining for passive
/// USB protocol discovery. This policy deliberately excludes controller/device
/// pointers and rundown descriptors while preserving only target UCX control-
/// transfer framing, completion status and transfer-data field metadata. It
/// never inspects field values.
/// </summary>
internal static class UsbEtwSchemaRetentionPolicy
{
    public const string SelectionPolicyId = "ucx-control-body-status-transfer-metadata-v1";
    private const string UcxProvider = "Microsoft-Windows-USB-UCX";
    private const string ClassInterfaceEventPrefix = "URB_FUNCTION_CLASS_INTERFACE";
    private const string ControlTransferEventPrefix = "URB_FUNCTION_CONTROL_TRANSFER";
    private const string ControlTransferFieldPrefix = "fid_UCX_URB_CONTROL_TRANSFER";
    private static readonly string[] DeniedNestedPathTokens =
    [
        "ContainerId",
        "Controller",
        "Device",
        "DevicePath",
        "Handle",
        "HcdArea",
        "InstanceId",
        "Link",
        "Mdl",
        "Pipe",
        "Pointer",
        "Ptr",
        "ReservedHcd",
        "TransferBuffer",
    ];

    public static UsbEtwSchemaRetentionClass Classify(
        string providerName,
        string eventName,
        string fieldName)
    {
        ArgumentNullException.ThrowIfNull(providerName);
        ArgumentNullException.ThrowIfNull(eventName);
        ArgumentNullException.ThrowIfNull(fieldName);

        if (!providerName.Equals(UcxProvider, StringComparison.Ordinal) ||
            (!eventName.StartsWith(ClassInterfaceEventPrefix, StringComparison.Ordinal) &&
             !eventName.StartsWith(ControlTransferEventPrefix, StringComparison.Ordinal)))
        {
            return UsbEtwSchemaRetentionClass.None;
        }

        if (fieldName.Equals("fid_IRP_NtStatus", StringComparison.Ordinal) ||
            fieldName.Equals("fid_URB_TransferData", StringComparison.Ordinal) ||
            fieldName.Equals("fid_URB_TransferDataLength", StringComparison.Ordinal))
        {
            return UsbEtwSchemaRetentionClass.Priority;
        }

        if (!fieldName.Equals(ControlTransferFieldPrefix, StringComparison.Ordinal) &&
            !fieldName.StartsWith($"{ControlTransferFieldPrefix}_", StringComparison.Ordinal) &&
            !fieldName.StartsWith($"{ControlTransferFieldPrefix}.", StringComparison.Ordinal))
        {
            return UsbEtwSchemaRetentionClass.None;
        }

        var nestedPathStart = fieldName.IndexOf('.', StringComparison.Ordinal);
        if (nestedPathStart < 0)
        {
            return UsbEtwSchemaRetentionClass.Framing;
        }

        var nestedPath = fieldName[(nestedPathStart + 1)..];
        if (DeniedNestedPathTokens.Any(token => nestedPath.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
            nestedPath.Split('.').Any(segment =>
                segment.Equals("TransferBuffer", StringComparison.OrdinalIgnoreCase) ||
                segment.StartsWith("TransferBuffer[", StringComparison.OrdinalIgnoreCase)))
        {
            return UsbEtwSchemaRetentionClass.None;
        }

        return UsbEtwSchemaRetentionClass.Framing;
    }
}
