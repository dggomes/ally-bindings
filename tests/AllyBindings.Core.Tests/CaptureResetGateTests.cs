using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class CaptureResetGateTests
{
    [Fact]
    public async Task Does_not_enter_backend_gate_until_capture_teardown_completes()
    {
        var captureCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var operationGate = new SemaphoreSlim(1, 1);

        var acquire = CaptureResetGate.AcquireAfterCaptureAsync(
            captureCompletion.Task,
            operationGate).AsTask();

        Assert.False(acquire.IsCompleted);
        Assert.True(operationGate.Wait(0));
        operationGate.Release();

        captureCompletion.SetResult();
        await using var lease = await acquire;
        Assert.False(operationGate.Wait(0));
    }

    [Fact]
    public async Task Releases_backend_gate_when_reset_scope_exits()
    {
        using var operationGate = new SemaphoreSlim(1, 1);

        await using (await CaptureResetGate.AcquireAfterCaptureAsync(null, operationGate))
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

        var acquire = CaptureResetGate.AcquireAfterCaptureAsync(
            captureCompletion.Task,
            operationGate,
            cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquire);
        Assert.True(operationGate.Wait(0));
        operationGate.Release();
    }
}
