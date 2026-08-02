using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class UsbEtwSchemaRetentionPolicyTests
{
    [Theory]
    [InlineData("URB_FUNCTION_CLASS_INTERFACE_Start", "fid_UCX_URB_CONTROL_TRANSFER")]
    [InlineData("URB_FUNCTION_CLASS_INTERFACE_Start", "fid_UCX_URB_CONTROL_TRANSFER.RequestType")]
    [InlineData("URB_FUNCTION_CONTROL_TRANSFER_EX_Start", "fid_UCX_URB_CONTROL_TRANSFER_EX.TransferFlags")]
    public void Classifies_target_control_transfer_framing(string eventName, string fieldName)
    {
        Assert.Equal(
            UsbEtwSchemaRetentionClass.Framing,
            UsbEtwSchemaRetentionPolicy.Classify("Microsoft-Windows-USB-UCX", eventName, fieldName));
    }

    [Theory]
    [InlineData("fid_URB_TransferDataLength")]
    [InlineData("fid_URB_TransferData")]
    [InlineData("fid_IRP_NtStatus")]
    public void Reserves_priority_for_transfer_data_and_completion_status(string fieldName)
    {
        Assert.Equal(
            UsbEtwSchemaRetentionClass.Priority,
            UsbEtwSchemaRetentionPolicy.Classify(
                "Microsoft-Windows-USB-UCX",
                "URB_FUNCTION_CONTROL_TRANSFER_Stop",
                fieldName));
    }

    [Theory]
    [InlineData("Microsoft-Windows-USB-USBXHCI", "USBXHCICommand_Information", "fid_Command_TRB")]
    [InlineData("Microsoft-Windows-USB-USBXHCI", "USBXHCIDeviceRundown_Information", "FirmwareHashFromDevice")]
    [InlineData("Microsoft-Windows-USB-USBHUB3", "USB3.0HubDriverRundown_Information", "fid_ConfigurationDescriptor")]
    [InlineData("Microsoft-Windows-USB-UCX", "UCXDeviceRundown_Information", "fid_UCX_URB_CONTROL_TRANSFER")]
    [InlineData("Microsoft-Windows-USB-UCX", "URB_FUNCTION_BULK_OR_INTERRUPT_TRANSFER_Start", "fid_UCX_URB_CONTROL_TRANSFER")]
    [InlineData("Microsoft-Windows-USB-UCX", "URB_FUNCTION_GET_DESCRIPTOR_FROM_DEVICE_Start", "fid_UCX_URB_CONTROL_TRANSFER")]
    [InlineData("Microsoft-Windows-USB-UCX", "URB_FUNCTION_CLASS_INTERFACE_Start", "fid_UcxController")]
    [InlineData("Microsoft-Windows-USB-UCX", "URB_FUNCTION_CLASS_INTERFACE_Start", "fid_UsbDevice")]
    [InlineData("Microsoft-Windows-USB-UCX", "URB_FUNCTION_CLASS_INTERFACE_Start", "fid_PipeHandle")]
    [InlineData("Microsoft-Windows-USB-UCX", "URB_FUNCTION_CLASS_INTERFACE_Start", "fid_IRP_Ptr")]
    [InlineData("Microsoft-Windows-USB-UCX", "URB_FUNCTION_CLASS_INTERFACE_Start", "fid_URB_Ptr")]
    [InlineData("Microsoft-Windows-USB-UCX", "URB_FUNCTION_CLASS_INTERFACE_Start", "fid_UCX_URB_CONTROL_TRANSFER.PipeHandle")]
    [InlineData("Microsoft-Windows-USB-UCX", "URB_FUNCTION_CLASS_INTERFACE_Start", "fid_UCX_URB_CONTROL_TRANSFER.fid_IRP_Ptr")]
    [InlineData("Microsoft-Windows-USB-UCX", "URB_FUNCTION_CLASS_INTERFACE_Start", "fid_UCX_URB_CONTROL_TRANSFER.DevicePath")]
    [InlineData("Microsoft-Windows-USB-UCX", "URB_FUNCTION_CLASS_INTERFACE_Start", "fid_UCX_URB_CONTROL_TRANSFER.TransferBuffer")]
    [InlineData("Microsoft-Windows-USB-UCX", "URB_FUNCTION_CLASS_INTERFACE_Start", "fid_UCX_URB_CONTROL_TRANSFER.TransferBuffer[0]")]
    [InlineData("Microsoft-Windows-USB-UCX", "URB_FUNCTION_CONTROL_TRANSFER_Stop", "fid_URB_TransferDataPointer")]
    public void Rejects_non_target_rundown_command_and_identity_pointer_metadata(
        string providerName,
        string eventName,
        string fieldName)
    {
        Assert.Equal(
            UsbEtwSchemaRetentionClass.None,
            UsbEtwSchemaRetentionPolicy.Classify(providerName, eventName, fieldName));
    }
}
