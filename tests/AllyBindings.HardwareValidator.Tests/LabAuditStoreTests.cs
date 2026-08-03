using System.Text.Json;

namespace AllyBindings.HardwareValidator.Tests;

public sealed class LabAuditStoreTests
{
    [Fact]
    public async Task Machine_wide_claim_is_atomic_durable_and_fail_closed()
    {
        var root = NewRoot();
        try
        {
            var audit = CreateAudit("claim-session", "pre-write");
            var attempts = await Task.WhenAll(
                AttemptClaimAsync(root, audit),
                AttemptClaimAsync(root, audit));

            Assert.Single(attempts, succeeded => succeeded);
            Assert.Single(attempts, succeeded => !succeeded);

            var claimPath = Path.Combine(root, "one-shot-claimed.json");
            Assert.True(File.Exists(claimPath));
            using (var claim = JsonDocument.Parse(await File.ReadAllTextAsync(claimPath)))
            {
                Assert.Equal(3, claim.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.True(claim.RootElement.GetProperty("recoveryRequired").GetBoolean());
                Assert.False(claim.RootElement.GetProperty("armouryRecoveryConfirmed").GetBoolean());
                Assert.Equal(audit.WirePacketSha256, claim.RootElement.GetProperty("WirePacketSha256").GetString());
            }

            await Assert.ThrowsAsync<IOException>(() =>
                LabAuditStore.ClaimOneShotForTestAsync(root, audit, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Partial_existing_claim_and_duplicate_audits_remain_fail_closed()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "one-shot-claimed.json"), "{");
            var audit = CreateAudit("partial-session", "pending-set-feature");

            await Assert.ThrowsAsync<IOException>(() =>
                LabAuditStore.ClaimOneShotForTestAsync(root, audit, CancellationToken.None));

            var preWritePath = await LabAuditStore.WriteForTestAsync(
                root,
                audit with { Outcome = "pre-write" },
                CancellationToken.None);
            var path = await LabAuditStore.WriteForTestAsync(root, audit, CancellationToken.None);
            Assert.True(File.Exists(preWritePath));
            Assert.True(File.Exists(path));
            using (var preWrite = JsonDocument.Parse(await File.ReadAllTextAsync(preWritePath)))
                Assert.Equal("pre-write", preWrite.RootElement.GetProperty("Outcome").GetString());
            using (var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
                Assert.Equal("pending-set-feature", persisted.RootElement.GetProperty("Outcome").GetString());

            await Assert.ThrowsAsync<IOException>(() =>
                LabAuditStore.WriteForTestAsync(root, audit, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<bool> AttemptClaimAsync(string root, LabAudit audit)
    {
        try
        {
            await LabAuditStore.ClaimOneShotForTestAsync(root, audit, CancellationToken.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static LabAudit CreateAudit(string sessionId, string outcome) => new(
        SchemaVersion: 3,
        ValidatorVersion: "test",
        SessionId: sessionId,
        CreatedAtUtc: DateTimeOffset.UtcNow,
        Model: "RC73XA",
        VendorId: HardwareLabPolicy.TargetVendorId,
        ProductId: HardwareLabPolicy.TargetProductId,
        CompatibleInterfaceCount: 1,
        FeatureReportLength: HardwareLabPolicy.LogicalReportLength,
        FixedMapping: "M1=A; M2=B",
        LogicalPacketSha256: HardwareLabPolicy.LogicalPacketSha256,
        WirePacketHex: HardwareLabPolicy.ToHex(HardwareLabPolicy.BuildWirePacket(50)),
        WirePacketSha256: HardwareLabPolicy.Sha256Hex(HardwareLabPolicy.BuildWirePacket(50)),
        Outcome: outcome,
        Detail: "test",
        AttemptedInterfaces: 0,
        SuccessfulInterfaces: 0,
        RecoveryRequired: true,
        ArmouryRecoveryConfirmed: false);

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), $"ally-validator-audit-{Guid.NewGuid():N}");
}
