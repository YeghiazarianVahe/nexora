namespace Nexora.Application.Services;

/// <summary>Singleton budget for on-demand detail reads. An abandoned synchronous WMI call retains its slot until it actually exits.</summary>
public sealed class DetailReadGate(TimeSpan? timeout = null)
{
    public const int MaxConcurrent = 4;
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(45);

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        var ownsSlot = false;
        try
        {
            await _slots.WaitAsync(deadline.Token);
            ownsSlot = true;
            deadline.Token.ThrowIfCancellationRequested();
            var work = read(deadline.Token);
            ownsSlot = false;
            _ = work.ContinueWith(t => { _ = t.Exception; _slots.Release(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return await work.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The workstation detail read exceeded its time budget.");
        }
        finally
        {
            if (ownsSlot) _slots.Release();
        }
    }
}
