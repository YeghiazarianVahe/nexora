using FluentAssertions;
using Nexora.Application.Services;

namespace Nexora.Application.Tests;

public sealed class DetailReadGateTests
{
    [Fact]
    public async Task Timed_out_native_calls_keep_their_slots_until_they_finish()
    {
        var gate = new DetailReadGate(TimeSpan.FromMilliseconds(100));
        var native = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        Task<int> Read(CancellationToken _) { Interlocked.Increment(ref started); return native.Task; }
        var reads = Enumerable.Range(0, DetailReadGate.MaxConcurrent).Select(_ => gate.RunAsync(Read, CancellationToken.None)).ToArray();
        foreach (var read in reads) await FluentActions.Awaiting(() => read).Should().ThrowAsync<TimeoutException>();
        await FluentActions.Awaiting(() => gate.RunAsync(Read, CancellationToken.None)).Should().ThrowAsync<TimeoutException>();
        started.Should().Be(DetailReadGate.MaxConcurrent);
        native.SetResult(7);
        await Task.WhenAll(reads.Select(t => t.ContinueWith(_ => { })));
        // The native task's continuation releases capacity even though its original HTTP request timed out.
        (await gate.RunAsync(_ => Task.FromResult(8), CancellationToken.None)).Should().Be(8);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_and_cancelled_reads_do_not_start()
    {
        var gate = new DetailReadGate();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var called = false;
        await FluentActions.Awaiting(() => gate.RunAsync(_ => { called = true; return Task.FromResult(1); }, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        called.Should().BeFalse();
    }

    [Fact]
    public async Task Synchronous_failure_releases_capacity()
    {
        var gate = new DetailReadGate();
        for (var i = 0; i < DetailReadGate.MaxConcurrent + 1; i++)
            await FluentActions.Awaiting(() => gate.RunAsync<int>(_ => throw new InvalidOperationException(), CancellationToken.None))
                .Should().ThrowAsync<InvalidOperationException>();
        (await gate.RunAsync(_ => Task.FromResult(1), CancellationToken.None)).Should().Be(1);
    }
}
