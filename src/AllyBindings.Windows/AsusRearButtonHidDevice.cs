using AllyBindings.Core;
using System.Collections.Immutable;
using HidSharp;
using HidSharp.Reports;
using Microsoft.Win32;
using System.Security.Cryptography;

namespace AllyBindings.Windows;

/// <summary>
/// Narrow, positively gated adapter for the ASUS embedded-controller feature
/// report used by the Ally family. It never opens a HID device unless both the
/// machine model and report descriptor match.
/// </summary>
public sealed class AsusRearButtonHidDevice : IAsusRearButtonDevice
{
    private static readonly TimeSpan HidOperationTimeout = TimeSpan.FromSeconds(3);
    private const int AsusVendorId = 0x0B05;
    private const string AsusManufacturer = "ASUSTeK COMPUTER INC.";

    private static readonly int[] EmbeddedControllerProductIds =
    [
        0x1ABE,
        0x1B4C,
        0x1B6E,
    ];
    private readonly SemaphoreSlim _hidIoGate = new(1, 1);
    private IReadOnlyList<string> _snapshotInterfaceIdentityKeys = [];
    private AsusRearButtonDeviceStatus _status = new(
        false,
        false,
        "Unknown",
        [],
        "ASUS rear-button hardware has not been probed yet.");

    public async Task<AsusRearButtonDeviceStatus> InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lease = new HidOperationLease();
        var operation = Task.Run(() =>
        {
            _hidIoGate.Wait();
            try
            {
                if (lease.IsCancelled) return _status;
                return ProbeSystemAndDevice();
            }
            finally
            {
                _hidIoGate.Release();
            }
        });

        if (await Task.WhenAny(operation, Task.Delay(HidOperationTimeout, cancellationToken)).ConfigureAwait(false) == operation)
        {
            _status = await operation.ConfigureAwait(false);
            return _status;
        }

        lease.Cancel();
        ObserveLateFailure(operation);
        cancellationToken.ThrowIfCancellationRequested();
        _status = _status with
        {
            IsAvailable = false,
            Message = "ASUS HID discovery timed out; no hardware write was attempted.",
        };
        return _status;
    }

    private AsusRearButtonDeviceStatus ProbeSystemAndDevice()
    {
        var identity = ReadSystemIdentity();
        var model = identity.ProductName.Trim();
        var manufacturerMatches =
            identity.Manufacturer.Trim().Equals(AsusManufacturer, StringComparison.OrdinalIgnoreCase);
        var modelMatches = AsusAllyModelIdentity.IsSupportedProductName(model);
        if (!manufacturerMatches || !modelMatches)
        {
            _snapshotInterfaceIdentityKeys = [];
            return new(
                false,
                false,
                model,
                [],
                $"System identity '{identity.Manufacturer}' / '{model}' is not a positively identified ROG Ally model.");
        }

        var devices = FindCompatibleDevices();
        var ids = DescribeDevices(devices);
        _snapshotInterfaceIdentityKeys = devices.Select(BuildInterfaceIdentityKey).ToArray();
        return new(
            true,
            devices.Count > 0,
            model,
            ids,
            devices.Count > 0
                ? $"Found {devices.Count} compatible ASUS embedded-controller HID interface(s)."
                : "The Ally model matched, but no openable ASUS feature-report 0x5A interface was found.");
    }

    public AsusRearButtonDeviceStatus GetStatus() => _status;

    internal IReadOnlyList<string> GetSnapshotInterfaceIdentityKeys() =>
        _snapshotInterfaceIdentityKeys.ToArray();

    /// <summary>
    /// Reads report 0x5A from every positively identified compatible interface.
    /// This path calls GetFeature only and is structurally separate from writes.
    /// </summary>
    public async Task<AsusRearButtonReadResult> ReadFeatureReportAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_status.IsSupportedModel)
        {
            throw new InvalidOperationException("Refusing an ASUS HID read on an unsupported system model.");
        }
        if (!_status.IsAvailable)
        {
            return new(false, false, [], "No compatible ASUS report 0x5A interface is available.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var lease = new HidOperationLease();
        var operation = Task.Run(() =>
        {
            _hidIoGate.Wait();
            try
            {
                if (lease.IsCancelled)
                {
                    return new AsusRearButtonReadResult(false, false, [], "Cancelled before the HID read began.");
                }
                return ReadFeatureReports();
            }
            finally
            {
                _hidIoGate.Release();
            }
        });

        if (await Task.WhenAny(operation, Task.Delay(HidOperationTimeout, cancellationToken)).ConfigureAwait(false) == operation)
        {
            return await operation.ConfigureAwait(false);
        }

        lease.Cancel();
        ObserveLateFailure(operation);
        cancellationToken.ThrowIfCancellationRequested();
        return new(true, false, [], "The read-only ASUS HID snapshot timed out after 3 seconds; no retry was attempted.");
    }

    public async Task<AsusRearButtonWriteResult> WriteFeatureReportAsync(
        byte[] report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Length != AsusRearButtonProtocol.ReportLength ||
            report[0] != AsusRearButtonProtocol.FeatureReportId)
        {
            throw new ArgumentException("Refusing an invalid ASUS M1/M2 feature report.", nameof(report));
        }
        if (!_status.IsSupportedModel)
        {
            throw new InvalidOperationException("Refusing an ASUS HID write on an unsupported system model.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var immutableReport = report.ToArray();
        var lease = new HidOperationLease();
        var operation = Task.Run(() =>
        {
            _hidIoGate.Wait();
            try
            {
                if (lease.IsCancelled)
                {
                    return (Attempted: 0, Succeeded: 0, DeviceIds: Array.Empty<string>(), Message: "Cancelled before the HID write began.");
                }
                return WriteFeatureReport(immutableReport);
            }
            finally
            {
                _hidIoGate.Release();
            }
        });

        if (await Task.WhenAny(operation, Task.Delay(HidOperationTimeout, cancellationToken)).ConfigureAwait(false) == operation)
        {
            var attempt = await operation.ConfigureAwait(false);
            _status = _status with
            {
                IsAvailable = attempt.Succeeded > 0,
                DeviceIds = attempt.DeviceIds,
                Message = attempt.Message,
            };
            return new(attempt.Attempted, attempt.Succeeded, attempt.Message);
        }

        lease.Cancel();
        ObserveLateFailure(operation);
        cancellationToken.ThrowIfCancellationRequested();
        const string timeoutMessage =
            "The ASUS HID write timed out after 3 seconds; its hardware outcome is unknown and recovery remains required.";
        _status = _status with
        {
            IsAvailable = false,
            Message = timeoutMessage,
        };
        return new(_status.DeviceIds.Count, 0, timeoutMessage);
    }

    private static (int Attempted, int Succeeded, string[] DeviceIds, string Message) WriteFeatureReport(byte[] report)
    {
        var devices = FindCompatibleDevices();
        var succeeded = 0;
        var errors = new List<string>();
        foreach (var device in devices)
        {
            try
            {
                using var stream = device.Open();
                var payload = new byte[device.GetMaxFeatureReportLength()];
                report.CopyTo(payload, 0);
                stream.SetFeature(payload);
                succeeded++;
                break;
            }
            catch (Exception ex)
            {
                errors.Add($"PID_{device.ProductID:X4}: {ex.Message}");
            }
        }

        var ids = devices
            .Select(device => $"VID_{device.VendorID:X4}&PID_{device.ProductID:X4}:report_{AsusRearButtonProtocol.FeatureReportId:X2}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var message = succeeded > 0
            ? $"Wrote the M1/M2 mapping to {succeeded} compatible ASUS interface(s)."
            : devices.Count == 0
                ? "The compatible ASUS M1/M2 interface disappeared before the write."
                : $"No ASUS interface accepted the M1/M2 mapping: {string.Join("; ", errors)}";

        return (devices.Count, succeeded, ids, message);
    }

    private static AsusRearButtonReadResult ReadFeatureReports()
    {
        var devices = FindCompatibleDevices();
        var reads = new List<AsusFeatureReportRead>(devices.Count);
        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            var deviceId = DescribeDevice(device, index);
            var reportLength = device.GetMaxFeatureReportLength();
            if (reportLength is < AsusRearButtonProtocol.ReportLength or > UsbEtwHidFeatureReportExtractor.MaximumWireReportLength)
            {
                reads.Add(new(deviceId, ImmutableArray<byte>.Empty, string.Empty, "Descriptor report length is outside the bounded 50-64 byte contract."));
                continue;
            }

            try
            {
                using var stream = device.Open();
                var buffer = new byte[reportLength];
                buffer[0] = AsusRearButtonProtocol.FeatureReportId;
                stream.GetFeature(buffer);
                reads.Add(new(
                    deviceId,
                    ImmutableArray.CreateRange(buffer),
                    Convert.ToHexString(SHA256.HashData(buffer)).ToLowerInvariant(),
                    "Read-only GET_FEATURE completed."));
            }
            catch (Exception ex)
            {
                reads.Add(new(
                    deviceId,
                    ImmutableArray<byte>.Empty,
                    string.Empty,
                    $"Read-only GET_FEATURE failed ({ex.GetType().Name}); no retry was attempted."));
            }
        }

        var succeeded = reads.Count(read => read.Report.Length > 0);
        return new(
            Attempted: devices.Count > 0,
            Succeeded: succeeded > 0,
            Reads: reads.AsReadOnly(),
            Message: devices.Count == 0
                ? "The compatible ASUS report 0x5A interface disappeared before the read."
                : $"Read report 0x5A from {succeeded}/{devices.Count} compatible interface(s).");
    }

    private static (string Manufacturer, string ProductName) ReadSystemIdentity()
    {
        try
        {
            var manufacturer = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS",
                    "SystemManufacturer",
                    null) as string
                ?? "Unknown";
            var productName = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS",
                    "SystemProductName",
                    null) as string
                ?? "Unknown";
            return (manufacturer, productName);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return ("Unknown", "Unknown");
        }
    }

    private static List<HidDevice> FindCompatibleDevices()
    {
        var devices = new List<HidDevice>();
        foreach (var productId in EmbeddedControllerProductIds)
        {
            foreach (var device in DeviceList.Local.GetHidDevices(AsusVendorId, productId))
            {
                try
                {
                    if (device.GetMaxFeatureReportLength() < AsusRearButtonProtocol.ReportLength)
                    {
                        continue;
                    }

                    if (device.GetReportDescriptor().TryGetReport(
                            ReportType.Feature,
                            AsusRearButtonProtocol.FeatureReportId,
                            out var featureReport) &&
                        featureReport.Length >= AsusRearButtonProtocol.ReportLength &&
                        device.TryOpen(out var probeStream))
                    {
                        probeStream.Dispose();
                        devices.Add(device);
                    }
                }
                catch (Exception)
                {
                    // Interfaces can disappear or become exclusively held between
                    // enumeration and descriptor/open checks. Skip them; writes are
                    // allowed only to interfaces that pass every positive gate.
                }
            }
        }
        return devices
            .OrderBy(device => device.GetFileSystemName(), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string[] DescribeDevices(IReadOnlyList<HidDevice> devices) =>
        devices.Select(DescribeDevice).ToArray();

    private static string DescribeDevice(HidDevice device, int index) =>
        $"VID_{device.VendorID:X4}&PID_{device.ProductID:X4}:report_{AsusRearButtonProtocol.FeatureReportId:X2}:interface_{index + 1}";

    private static string BuildInterfaceIdentityKey(HidDevice device)
    {
        var normalizedPath = device.GetFileSystemName().Trim().ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedPath)));
    }

    private static void ObserveLateFailure<T>(Task<T> operation)
    {
        _ = operation.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class HidOperationLease
    {
        private int _cancelled;
        public bool IsCancelled => Volatile.Read(ref _cancelled) != 0;
        public void Cancel() => Interlocked.Exchange(ref _cancelled, 1);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
