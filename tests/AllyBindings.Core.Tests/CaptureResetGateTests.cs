using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class CaptureResetGateTests
{
    [Fact]
    public async Task Cancels_and_awaits_active_capture_before_entering_backend_gate()
    {
        var captureCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var operationGate = new SemaphoreSlim(1, 1);
        var captureActive = true;
        var cancellationRequested = false;

        var acquire = CaptureResetGate.AcquireWhenCaptureStoppedAsync(
            operationGate,
            () => captureActive ? captureCompletion.Task : null,
            () => cancellationRequested = true).AsTask();

        await Task.Yield();
        Assert.True(cancellationRequested);
        Assert.False(acquire.IsCompleted);
        Assert.True(operationGate.Wait(0));
        operationGate.Release();

        captureActive = false;
        captureCompletion.SetResult();
        await using var lease = await acquire;
        Assert.False(operationGate.Wait(0));
    }

    [Fact]
    public async Task Rechecks_capture_state_after_waiting_for_operation_gate()
    {
        using var operationGate = new SemaphoreSlim(1, 1);
        await operationGate.WaitAsync();
        var captureCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureActive = false;
        var cancellationRequested = false;
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var acquire = CaptureResetGate.AcquireWhenCaptureStoppedAsync(
            operationGate,
            () => captureActive ? captureCompletion.Task : null,
            () =>
            {
                cancellationRequested = true;
                cancellationObserved.SetResult();
            }).AsTask();

        // Simulate a capture queued ahead of reset: it starts while reset is waiting,
        // then releases the shared gate for reset to inspect the new active state.
        captureActive = true;
        operationGate.Release();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(cancellationRequested);
        Assert.False(acquire.IsCompleted);

        captureActive = false;
        captureCompletion.SetResult();
        await using var lease = await acquire;
        Assert.False(operationGate.Wait(0));
    }

    [Fact]
    public async Task Releases_backend_gate_when_reset_scope_exits()
    {
        using var operationGate = new SemaphoreSlim(1, 1);

        await using (await CaptureResetGate.AcquireWhenCaptureStoppedAsync(
                         operationGate,
                         () => null,
                         () => throw new InvalidOperationException("No capture should be cancelled.")))
        {
            Assert.False(operationGate.Wait(0));
        }

        Assert.True(operationGate.Wait(0));
        operationGate.Release();
    }

    [Fact]
    public async Task Cancellation_prevents_entry_after_capture_teardown()
    {
        var captureCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var operationGate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();

        var acquire = CaptureResetGate.AcquireWhenCaptureStoppedAsync(
            operationGate,
            () => captureCompletion.Task,
            () => { },
            cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquire);
        Assert.True(operationGate.Wait(0));
        operationGate.Release();
    }
}
