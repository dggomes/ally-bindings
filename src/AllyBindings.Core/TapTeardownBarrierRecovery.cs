namespace AllyBindings.Core;

public enum TapTeardownBarrierRecoveryDecision
{
    RetainBarrier,
    EstablishBootBaseline,
    ClearBarrier,
}

/// <summary>
/// Makes fail-closed recovery decisions from Windows boot-session identifiers.
/// A missing historical identifier can be baselined but never treated as proof
/// that a reboot already happened.
/// </summary>
public static class TapTeardownBarrierRecovery
{
    public static TapTeardownBarrierRecoveryDecision Evaluate(
        Guid? blockedBootIdentifier,
        Guid? currentBootIdentifier)
    {
        var blocked = blockedBootIdentifier is { } blockedCandidate && blockedCandidate != Guid.Empty
            ? blockedCandidate
            : (Guid?)null;
        var current = currentBootIdentifier is { } currentCandidate && currentCandidate != Guid.Empty
            ? currentCandidate
            : (Guid?)null;

        if (blocked is not null && current is not null)
        {
            return blocked != current
                ? TapTeardownBarrierRecoveryDecision.ClearBarrier
                : TapTeardownBarrierRecoveryDecision.RetainBarrier;
        }

        return blocked is null && current is not null
            ? TapTeardownBarrierRecoveryDecision.EstablishBootBaseline
            : TapTeardownBarrierRecoveryDecision.RetainBarrier;
    }
}
