using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Nexora.Application;
using Nexora.Application.Services;

// Pure in-process fake collector. No URLs, WMI, directory access or production credentials.
var results=new List<object>();
foreach(var count in new[]{500,1000})
foreach(var shared in new[]{true,false})
{
    using var cache=new MemoryCache(new MemoryCacheOptions());
    var collector=new FakeCollector();var reader=new LivePerformanceReader(collector,cache,new Clock());
    var elapsed=Stopwatch.StartNew();int ok=0,failed=0;
    await Task.WhenAll(Enumerable.Range(0,count).Select(async i=>{
        try{await reader.ReadAsync(shared?"SHARED":"SYNTHETIC-"+i,CancellationToken.None);Interlocked.Increment(ref ok);}
        catch(Exception ex) when(ex is OperationCanceledException or TimeoutException){Interlocked.Increment(ref failed);}
    }));
    results.Add(new{clients=count,shared,ok,failed,collector.Calls,collector.MaximumActive,elapsedMs=elapsed.ElapsedMilliseconds,capacity=reader.Snapshot.Capacity});
    if(collector.MaximumActive>4 || (shared && collector.Calls!=1)) throw new InvalidOperationException("Collector concurrency or shared-host invariant failed");
}
Console.WriteLine(JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
// Exercise adverse conditions independently from the throughput cases above.
foreach (var scenario in new[]{"slow", "offline", "cancellation", "saturation"})
{
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var collector = new ScenarioCollector(scenario, release.Task);
    var reader = new LivePerformanceReader(collector, cache, new Clock());
    using var viewer = new CancellationTokenSource();
    var count = scenario == "saturation" ? 1000 : 100;
    var tasks = Enumerable.Range(0, count).Select(async i => {
        try { await reader.ReadAsync(scenario == "cancellation" ? "SHARED-CANCEL" : "FAKE-"+i,
            scenario == "cancellation" && i % 2 == 0 ? viewer.Token : CancellationToken.None); return "ok"; }
        catch (OperationCanceledException) { return "canceled"; }
        catch (TimeoutException) { return "timeout"; }
        catch (IOException) { return "offline"; }
    }).ToArray();
    viewer.Cancel();
    if (scenario == "cancellation") release.SetResult();
    var outcomes = await Task.WhenAll(tasks);
    // Uncooperative collectors must keep their slots even after callers time out.
    if (scenario == "saturation")
    {
        if (collector.MaximumActive != 4 || collector.Calls != 4) throw new InvalidOperationException("Abandoned collectors lost their slots");
        release.SetResult();
    }
    for (var i=0; i<200 && (reader.Snapshot.Active != 0 || reader.Snapshot.Queued != 0); i++) await Task.Delay(10);
    if (reader.Snapshot.Active != 0 || reader.Snapshot.Queued != 0 || collector.MaximumActive > 4)
        throw new InvalidOperationException("Worker leak or capacity violation");
    if (scenario == "offline" && outcomes.Any(x=>x!="offline")) throw new InvalidOperationException("Offline errors lost");
    if (scenario == "cancellation" && (outcomes.Count(x=>x=="ok")!=50 || outcomes.Count(x=>x=="canceled")!=50 || collector.Calls!=1))
        throw new InvalidOperationException("Viewer cancellation corrupted shared work");
    // Offline failures must be cached during backoff rather than re-probed.
    if (scenario == "offline") {
        var before=collector.Calls;
        try { await reader.ReadAsync("FAKE-0", CancellationToken.None); } catch(IOException) { }
        if(collector.Calls!=before) throw new InvalidOperationException("Offline backoff failed");
    }
    Console.WriteLine(JsonSerializer.Serialize(new {scenario, clients=count, outcomes=outcomes.GroupBy(x=>x).ToDictionary(g=>g.Key,g=>g.Count()), collector.Calls, collector.MaximumActive, active=reader.Snapshot.Active, queued=reader.Snapshot.Queued}));
}
sealed class Clock:ISystemClock{public DateTime UtcNow=>DateTime.UtcNow;}
sealed class FakeCollector:IPerformanceCollector
{
    private int _active;public int Calls,MaximumActive;
    public async Task<LiveSampleDto> CollectPerformanceAsync(string host,CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);var active=Interlocked.Increment(ref _active);
        int previous;do{previous=MaximumActive;if(previous>=active)break;}while(Interlocked.CompareExchange(ref MaximumActive,active,previous)!=previous);
        try{await Task.Delay(5,ct);return new(15,1000,400,600,40,DateTime.UtcNow);}
        finally{Interlocked.Decrement(ref _active);}
    }
}
sealed class ScenarioCollector(string scenario, Task release):IPerformanceCollector
{
    private int active;
    public int Calls, MaximumActive;
    public async Task<LiveSampleDto> CollectPerformanceAsync(string host,CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        var current=Interlocked.Increment(ref active);
        int prior; do { prior=MaximumActive; if(prior>=current) break; } while(Interlocked.CompareExchange(ref MaximumActive,current,prior)!=prior);
        try {
            if(scenario=="offline") throw new IOException("Synthetic offline workstation");
            if(scenario is "saturation" or "cancellation") await release; // deliberately ignores collector cancellation
            else await Task.Delay(200,ct);
            return new(15,1000,400,600,40,DateTime.UtcNow);
        } finally { Interlocked.Decrement(ref active); }
    }
}
