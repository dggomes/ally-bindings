namespace AllyBindings.Core;

/// <summary>
/// Serializes a reset behind capture teardown and the shared backend operation gate.
/// </summary>
public static class CaptureResetGate
{
    public static async ValueTask<IAsyncDisposable> AcquireAfterCaptureAsync(
        Task? captureCompletion,
        SemaphoreSlim operationGate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationGate);
        if (captureCompletion is not null)
        {
            await captureCompletion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(operationGate);
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
