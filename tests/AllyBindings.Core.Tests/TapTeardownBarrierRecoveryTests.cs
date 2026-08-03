using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class TapTeardownBarrierRecoveryTests
{
    private static readonly Guid BlockedBoot = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CurrentBoot = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Different_windows_boot_identifiers_prove_restart()
    {
        Assert.Equal(
            TapTeardownBarrierRecoveryDecision.ClearBarrier,
            TapTeardownBarrierRecovery.Evaluate(BlockedBoot, CurrentBoot));
    }

    [Fact]
    public void Same_windows_boot_identifier_retains_barrier()
    {
        Assert.Equal(
            TapTeardownBarrierRecoveryDecision.RetainBarrier,
            TapTeardownBarrierRecovery.Evaluate(BlockedBoot, BlockedBoot));
    }

    [Fact]
    public void Missing_preview18_identifier_establishes_current_boot_baseline()
    {
        Assert.Equal(
            TapTeardownBarrierRecoveryDecision.EstablishBootBaseline,
            TapTeardownBarrierRecovery.Evaluate(null, CurrentBoot));
    }

    [Fact]
    public void Empty_historical_identifier_establishes_baseline_without_clearing()
    {
        Assert.Equal(
            TapTeardownBarrierRecoveryDecision.EstablishBootBaseline,
            TapTeardownBarrierRecovery.Evaluate(Guid.Empty, CurrentBoot));
    }

    [Fact]
    public void Empty_current_identifier_cannot_clear_barrier()
    {
        Assert.Equal(
            TapTeardownBarrierRecoveryDecision.RetainBarrier,
            TapTeardownBarrierRecovery.Evaluate(BlockedBoot, Guid.Empty));
    }

    [Fact]
    public void Missing_current_identifier_cannot_clear_or_rebaseline_barrier()
    {
        Assert.Equal(
            TapTeardownBarrierRecoveryDecision.RetainBarrier,
            TapTeardownBarrierRecovery.Evaluate(BlockedBoot, null));
    }

    [Fact]
    public void No_boot_identifiers_retains_barrier()
    {
        Assert.Equal(
            TapTeardownBarrierRecoveryDecision.RetainBarrier,
            TapTeardownBarrierRecovery.Evaluate(null, null));
    }
}
