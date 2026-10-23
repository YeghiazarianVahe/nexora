using Microsoft.Extensions.Caching.Memory;

namespace Nexora.Application.Services;

public interface IPerformanceCollector
{
    /// <summary>CPU and RAM only; no inventory, disk scans or process enumeration.</summary>
    Task<LiveSampleDto> CollectPerformanceAsync(string host, CancellationToken ct);
}

/// <summary>Shares on-screen reads across viewers. An abandoned WMI call retains its slot and per-host task until it actually finishes.</summary>
public sealed class LivePerformanceReader(IPerformanceCollector collector, IMemoryCache cache, ISystemClock clock)
{
    private static readonly System.Diagnostics.Metrics.Meter Meter = new("Nexora.LivePerformance", "1.0");
    private static readonly System.Diagnostics.Metrics.Counter<long> Requests = Meter.CreateCounter<long>("nexora.live.requests");
    private static readonly System.Diagnostics.Metrics.Counter<long> Failures = Meter.CreateCounter<long>("nexora.live.failures");
    private static readonly System.Diagnostics.Metrics.Histogram<double> Duration = Meter.CreateHistogram<double>("nexora.live.duration", "ms");
    private int _active, _queued;
    public (int Active, int Queued, int Capacity) Snapshot => (Volatile.Read(ref _active), Volatile.Read(ref _queued), 4);
    private sealed class Entry
    {
        public readonly object Sync = new();
        public Task<LiveSampleDto>? Task;
        public DateTime ReuseUntil;
    }
    private readonly SemaphoreSlim _slots = new(4, 4);
    private readonly object _cacheSync = new();

    public Task<LiveSampleDto> ReadAsync(string host, CancellationToken ct)
    {
        Requests.Add(1);
        Entry entry;
        lock (_cacheSync)
            entry = cache.GetOrCreate((typeof(LivePerformanceReader), host.ToUpperInvariant()), e =>
            {
                e.SlidingExpiration = TimeSpan.FromMinutes(10);
                return new Entry();
            })!;
        Task<LiveSampleDto> work;
        lock (entry.Sync)
        {
            if (entry.Task is null || (entry.Task.IsCompleted && clock.UtcNow >= entry.ReuseUntil))
                entry.Task = CollectAsync(host, entry);
            work = entry.Task;
        }
        return work.WaitAsync(TimeSpan.FromSeconds(3), ct);
    }

    private async Task<LiveSampleDto> CollectAsync(string host, Entry entry)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var ownsSlot = false;
        try
        {
            Interlocked.Increment(ref _queued);
            try { await _slots.WaitAsync(budget.Token); }
            finally { Interlocked.Decrement(ref _queued); }
            ownsSlot = true;
            Interlocked.Increment(ref _active);
            lock (entry.Sync) entry.ReuseUntil = clock.UtcNow.AddMilliseconds(900);
            return await collector.CollectPerformanceAsync(host, budget.Token);
        }
        catch
        {
            Failures.Add(1);
            lock (entry.Sync) entry.ReuseUntil = clock.UtcNow.AddSeconds(5);
            throw;
        }
        finally
        {
            Duration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (ownsSlot) { Interlocked.Decrement(ref _active); _slots.Release(); }
        }
    }
}
