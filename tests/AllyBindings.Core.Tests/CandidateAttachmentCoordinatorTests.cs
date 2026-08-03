using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class CandidateAttachmentCoordinatorTests
{
    [Fact]
    public async Task Safely_rejected_candidate_does_not_block_later_success()
    {
        var attached = new List<string>();
        var rejections = await CandidateAttachmentCoordinator.AttachAvailableAsync(
            ["reject", "writer"],
            attached,
            candidate => candidate,
            (candidate, _) => candidate == "reject"
                ? Task.FromException<string>(new InvalidOperationException("rejected"))
                : Task.FromResult($"attached:{candidate}"),
            ex => ex is UnsafeTeardownException,
            _ => "attach-rejected");

        Assert.Equal(["attached:writer"], attached);
        Assert.Equal([new CandidateAttachmentRejection("reject", "attach-rejected")], rejections);
    }

    [Fact]
    public async Task Fail_closed_error_preserves_already_attached_targets_for_caller_teardown()
    {
        var attached = new List<string>();

        await Assert.ThrowsAsync<UnsafeTeardownException>(() =>
            CandidateAttachmentCoordinator.AttachAvailableAsync(
                ["writer", "unsafe"],
                attached,
                candidate => candidate,
                (candidate, _) => candidate == "unsafe"
                    ? Task.FromException<string>(new UnsafeTeardownException())
                    : Task.FromResult($"attached:{candidate}"),
                ex => ex is UnsafeTeardownException,
                _ => "attach-rejected"));

        Assert.Equal(["attached:writer"], attached);
    }

    [Fact]
    public async Task All_safe_rejections_return_no_targets_and_bounded_reasons()
    {
        var attached = new List<string>();
        var rejections = await CandidateAttachmentCoordinator.AttachAvailableAsync(
            ["one", "two"],
            attached,
            candidate => candidate,
            (_, _) => Task.FromException<string>(new InvalidOperationException()),
            ex => ex is UnsafeTeardownException,
            _ => "attach-rejected");

        Assert.Empty(attached);
        Assert.Equal(
            [
                new CandidateAttachmentRejection("one", "attach-rejected"),
                new CandidateAttachmentRejection("two", "attach-rejected"),
            ],
            rejections);
    }

    [Fact]
    public async Task Caller_cancellation_is_never_downgraded_to_safe_rejection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var attached = new List<string>();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CandidateAttachmentCoordinator.AttachAvailableAsync(
                ["candidate"],
                attached,
                candidate => candidate,
                (_, token) => Task.FromCanceled<string>(token),
                _ => false,
                _ => "attach-rejected",
                cancellation.Token));

        Assert.Empty(attached);
    }

    private sealed class UnsafeTeardownException : Exception;
}
