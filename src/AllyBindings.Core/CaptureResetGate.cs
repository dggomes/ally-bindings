namespace AllyBindings.Core;

/// <summary>
/// Atomically waits for capture teardown and enters the shared backend operation gate.
/// Capture state is inspected only while holding that gate, preventing a queued capture
/// from starting between a reset's state snapshot and backend entry.
/// </summary>
public static class CaptureResetGate
{
    public static async ValueTask<IAsyncDisposable> AcquireWhenCaptureStoppedAsync(
        SemaphoreSlim operationGate,
        Func<Task?> getActiveCaptureCompletion,
        Action requestCaptureCancellation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationGate);
        ArgumentNullException.ThrowIfNull(getActiveCaptureCompletion);
        ArgumentNullException.ThrowIfNull(requestCaptureCancellation);

        while (true)
        {
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task? captureCompletion;
            try
            {
                captureCompletion = getActiveCaptureCompletion();
                if (captureCompletion is null) return new Lease(operationGate);
                requestCaptureCancellation();
            }
            catch
            {
                operationGate.Release();
                throw;
            }

            operationGate.Release();
            await captureCompletion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Lease(SemaphoreSlim operationGate) : IAsyncDisposable
    {
        private SemaphoreSlim? _operationGate = operationGate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _operationGate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
