using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class UsbEtwHidFeatureReportExtractorTests
{
    [Fact]
    public void Extracts_complete_report_from_a_metadata_decoded_binary_field()
    {
        var expected = AsusRearButtonProtocol.BuildMappingReport(ControllerButton.A, ControllerButton.B);
        var field = new byte[8 + expected.Length];
        expected.CopyTo(field, 8);
        var timestamp = DateTimeOffset.Parse("2026-08-01T12:00:00Z");

        var result = Extract(timestamp, new UsbEtwBinaryField("TransferBuffer", field));

        var report = Assert.Single(result.Reports);
        Assert.False(result.LimitExceeded);
        Assert.Equal(0, result.AmbiguousCandidateCount);
        Assert.Equal(timestamp, report.Timestamp);
        Assert.Equal("Microsoft-Windows-USB-UCX", report.ProviderName);
        Assert.Equal("ControlTransferData", report.EventName);
        Assert.Equal(68, report.EventId);
        Assert.Equal("TransferBuffer", report.SourceField);
        Assert.Equal(8, report.SourceOffset);
        Assert.Equal(expected, report.Report);
        Assert.Equal(64, report.Sha256.Length);
    }

    [Fact]
    public void Preserves_zero_padded_64_byte_wire_report_for_exact_validation()
    {
        var expected = AsusRearButtonProtocol.BuildMappingReport(ControllerButton.X, ControllerButton.Y);
        var wire = new byte[UsbEtwHidFeatureReportExtractor.MaximumWireReportLength];
        expected.CopyTo(wire, 0);

        var report = Assert.Single(Extract(DateTimeOffset.UtcNow, new UsbEtwBinaryField("urb_transfer_data", wire)).Reports);

        Assert.Equal(wire, report.Report);
        Assert.True(AsusRearButtonProtocol.MatchesWireReport(report.Report, expected));
    }

    [Fact]
    public void Rejects_nonzero_data_beyond_the_expected_vector_during_exact_validation()
    {
        var expected = AsusRearButtonProtocol.BuildMappingReport(ControllerButton.A, ControllerButton.B);
        var wire = new byte[UsbEtwHidFeatureReportExtractor.MaximumWireReportLength];
        expected.CopyTo(wire, 0);
        wire[^1] = 0xCC;

        var report = Assert.Single(Extract(DateTimeOffset.UtcNow, new UsbEtwBinaryField("TransferBuffer", wire)).Reports);

        Assert.False(AsusRearButtonProtocol.MatchesWireReport(report.Report, expected));
    }

    [Fact]
    public void Ignores_prefix_inside_an_ambiguously_large_binary_field()
    {
        var expected = AsusRearButtonProtocol.BuildMappingReport(ControllerButton.A, ControllerButton.B);
        var field = new byte[256];
        expected.CopyTo(field, 12);

        var result = Extract(DateTimeOffset.UtcNow, new UsbEtwBinaryField("RawEventData", field));
        Assert.Empty(result.Reports);
        Assert.Equal(1, result.AmbiguousCandidateCount);
    }

    [Fact]
    public void Ignores_feature_report_ids_without_the_command_prefix()
    {
        var payload = new byte[AsusRearButtonProtocol.ReportLength];
        payload[0] = AsusRearButtonProtocol.FeatureReportId;
        payload[1] = 0x99;

        Assert.Empty(Extract(DateTimeOffset.UtcNow, new UsbEtwBinaryField("TransferBuffer", payload)).Reports);
    }

    [Fact]
    public void Ignores_a_prefix_that_does_not_have_a_complete_report_after_it()
    {
        var payload = new byte[AsusRearButtonProtocol.ReportLength];
        payload[^5] = 0x5A;
        payload[^4] = 0xD1;
        payload[^3] = 0x02;
        payload[^2] = 0x08;
        payload[^1] = 0x2C;

        var result = Extract(DateTimeOffset.UtcNow, new UsbEtwBinaryField("TransferBuffer", payload));
        Assert.Empty(result.Reports);
        Assert.Equal(1, result.AmbiguousCandidateCount);
    }

    [Fact]
    public void Accepts_multiple_binary_fields_without_nonbinary_metadata()
    {
        var first = AsusRearButtonProtocol.BuildMappingReport(ControllerButton.A, ControllerButton.B);
        var second = AsusRearButtonProtocol.BuildMappingReport(ControllerButton.X, ControllerButton.Y);

        var result = Extract(
            DateTimeOffset.UtcNow,
            new UsbEtwBinaryField("Irp", new byte[8]),
            new UsbEtwBinaryField("FirstTransferBuffer", first),
            new UsbEtwBinaryField("SecondTransferBuffer", second));

        Assert.Equal(2, result.Reports.Count);
        Assert.Equal(first, result.Reports[0].Report);
        Assert.Equal(second, result.Reports[1].Report);
    }

    [Fact]
    public void Counts_ambiguous_coalesced_candidates_instead_of_silently_discarding_them()
    {
        var first = AsusRearButtonProtocol.BuildMappingReport(ControllerButton.A, ControllerButton.B);
        var second = AsusRearButtonProtocol.BuildMappingReport(ControllerButton.X, ControllerButton.Y);
        var coalesced = new byte[first.Length + second.Length];
        first.CopyTo(coalesced, 0);
        second.CopyTo(coalesced, first.Length);

        var result = Extract(DateTimeOffset.UtcNow, new UsbEtwBinaryField("TransferBuffer", coalesced));

        Assert.Single(result.Reports);
        Assert.Equal(second, result.Reports[0].Report);
        Assert.Equal(1, result.AmbiguousCandidateCount);
    }

    [Fact]
    public void Stops_allocating_and_sets_overflow_when_report_limit_is_reached()
    {
        var first = AsusRearButtonProtocol.BuildMappingReport(ControllerButton.A, ControllerButton.B);
        var second = AsusRearButtonProtocol.BuildMappingReport(ControllerButton.X, ControllerButton.Y);

        var result = UsbEtwHidFeatureReportExtractor.Extract(
            DateTimeOffset.UtcNow,
            "Microsoft-Windows-USB-UCX",
            "ControlTransferData",
            53,
            [new("first", first), new("second", second)],
            maximumReports: 1);

        Assert.Single(result.Reports);
        Assert.True(result.LimitExceeded);
    }

    [Fact]
    public void Rejects_blank_provider_names_and_nonpositive_limits()
    {
        Assert.Throws<ArgumentException>(() =>
            UsbEtwHidFeatureReportExtractor.Extract(
                DateTimeOffset.UtcNow,
                " ",
                "event",
                1,
                [],
                maximumReports: 16));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            UsbEtwHidFeatureReportExtractor.Extract(
                DateTimeOffset.UtcNow,
                "provider",
                "event",
                1,
                [],
                maximumReports: 0));
    }

    private static UsbEtwExtractionResult Extract(
        DateTimeOffset timestamp,
        params UsbEtwBinaryField[] fields) =>
        UsbEtwHidFeatureReportExtractor.Extract(
            timestamp,
            "Microsoft-Windows-USB-UCX",
            "ControlTransferData",
            68,
            fields,
            maximumReports: 16);
}
