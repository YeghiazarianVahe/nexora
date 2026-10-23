using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Nexora.Application;
using Nexora.Application.Services;

namespace Nexora.Application.Tests;

public sealed class LivePerformanceTests
{
    private sealed class Clock : ISystemClock { public DateTime UtcNow { get; set; } = DateTime.UtcNow; }
    private sealed class Collector(Func<string, CancellationToken, Task<LiveSampleDto>> read) : IPerformanceCollector
    {
        public int Calls;
        public Task<LiveSampleDto> CollectPerformanceAsync(string host, CancellationToken ct) { Interlocked.Increment(ref Calls); return read(host, ct); }
    }
    private static LiveSampleDto Sample() => new(12, 16_000_000_000, 8_000_000_000, 8_000_000_000, 50, DateTime.UtcNow);

    [Fact]
    public async Task Viewers_share_one_inflight_read_and_a_recent_result_then_get_a_fresh_sample()
    {
        var pending = new TaskCompletionSource<LiveSampleDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = new Collector((_, _) => pending.Task);
        var clock = new Clock();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var reader = new LivePerformanceReader(collector, cache, clock);
        var first = reader.ReadAsync("PC-1", CancellationToken.None);
        var second = reader.ReadAsync("pc-1", CancellationToken.None);
        collector.Calls.Should().Be(1);
        pending.SetResult(Sample());
        (await first).Should().BeSameAs(await second);
        await reader.ReadAsync("PC-1", CancellationToken.None);
        collector.Calls.Should().Be(1);
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        await reader.ReadAsync("PC-1", CancellationToken.None);
        collector.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Closing_one_viewer_does_not_cancel_a_read_shared_with_another_viewer()
    {
        var pending = new TaskCompletionSource<LiveSampleDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = new Collector((_, _) => pending.Task);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var reader = new LivePerformanceReader(collector, cache, new Clock());
        using var canceled = new CancellationTokenSource();
        var abandoned = reader.ReadAsync("PC-1", canceled.Token);
        var remaining = reader.ReadAsync("PC-1", CancellationToken.None);
        canceled.Cancel();
        await ((Func<Task>)(async () => await abandoned)).Should().ThrowAsync<OperationCanceledException>();
        pending.SetResult(Sample());
        (await remaining).CpuLoadPercent.Should().Be(12);
        collector.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Failed_reads_back_off_instead_of_hammering_an_unreachable_workstation()
    {
        var collector = new Collector((_, _) => Task.FromException<LiveSampleDto>(new IOException("offline")));
        var clock = new Clock();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var reader = new LivePerformanceReader(collector, cache, clock);
        for (var i = 0; i < 3; i++)
            await ((Func<Task>)(async () => await reader.ReadAsync("PC-1", CancellationToken.None))).Should().ThrowAsync<IOException>();
        collector.Calls.Should().Be(1);
        clock.UtcNow = clock.UtcNow.AddSeconds(6);
        await ((Func<Task>)(async () => await reader.ReadAsync("PC-1", CancellationToken.None))).Should().ThrowAsync<IOException>();
        collector.Calls.Should().Be(2);
    }
}
