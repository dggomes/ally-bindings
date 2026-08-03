namespace AllyBindings.Core;

public sealed record CandidateAttachmentRejection(string CandidateName, string Reason);

/// <summary>
/// Tries a bounded set of independently verified candidates without making an ordinary,
/// safely rolled-back attachment rejection fatal to candidates that can still attach.
/// Successfully attached targets are added to the caller-owned sink immediately so a later
/// fail-closed exception cannot hide targets that still require teardown.
/// </summary>
public static class CandidateAttachmentCoordinator
{
    public static async Task<IReadOnlyList<CandidateAttachmentRejection>> AttachAvailableAsync<TCandidate, TTarget>(
        IReadOnlyList<TCandidate> candidates,
        List<TTarget> attachedTargets,
        Func<TCandidate, string> getCandidateName,
        Func<TCandidate, CancellationToken, Task<TTarget>> attach,
        Func<Exception, bool> requiresFailClosedAbort,
        Func<Exception, string> describeSafeRejection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(attachedTargets);
        ArgumentNullException.ThrowIfNull(getCandidateName);
        ArgumentNullException.ThrowIfNull(attach);
        ArgumentNullException.ThrowIfNull(requiresFailClosedAbort);
        ArgumentNullException.ThrowIfNull(describeSafeRejection);
        if (attachedTargets.Count != 0)
            throw new ArgumentException("The attached-target sink must be empty at startup.", nameof(attachedTargets));
        attachedTargets.EnsureCapacity(candidates.Count);

        var rejections = new List<CandidateAttachmentRejection>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TTarget attachedTarget;
            try
            {
                attachedTarget = await attach(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!requiresFailClosedAbort(ex))
            {
                var name = getCandidateName(candidate);
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException("A rejected attachment candidate has no diagnostic name.");
                var reason = describeSafeRejection(ex);
                if (string.IsNullOrWhiteSpace(reason))
                    throw new InvalidOperationException("A safely rejected attachment has no diagnostic reason.");
                rejections.Add(new(name, reason));
                continue;
            }
            attachedTargets.Add(attachedTarget);
        }
        return rejections;
    }
}
