using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Services;

/// <summary>
/// Polls every endpoint over WMI on a fixed cadence. A producer feeds a bounded Channel and a SemaphoreSlim
/// caps concurrent collections (default 32); each result is stored and pushed to SignalR as a delta.
/// </summary>
public sealed class MonitoringService(
    IServiceScopeFactory scopes,
    IEndpointCollector collector,
    INexoraNotifier notifier,
    OuBackfillService ouBackfill,
    IOptions<NexoraOptions> options,
    ISystemClock clock,
    ILogger<MonitoringService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.Value.Monitoring.Enabled)
        {
            logger.LogInformation("Monitoring is disabled by configuration");
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(5), stop); }
        catch (OperationCanceledException) { return; }

        while (!stop.IsCancellationRequested)
        {
            var started = clock.UtcNow;
            PlatformSettings settings;
            try
            {
                using var scope = scopes.CreateScope();
                settings = await scope.ServiceProvider.GetRequiredService<ISettingsService>().GetAsync(stop);
                await RunCycleAsync(settings, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Monitoring cycle failed");
                settings = new PlatformSettings();
            }

            var wait = TimeSpan.FromSeconds(settings.PollIntervalSeconds) - (clock.UtcNow - started);
            try { await Task.Delay(wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait, stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunCycleAsync(PlatformSettings settings, CancellationToken stop)
    {
        await ouBackfill.RunAsync(stop);
        Interlocked.Exchange(ref _diskScansThisCycle, 0);
        List<(Guid Id, string Host)> targets;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            targets = (await db.Endpoints.AsNoTracking().Select(e => new { e.Id, e.Hostname }).ToListAsync(stop))
                .Select(e => (e.Id, e.Hostname)).ToList();
        }
        if (targets.Count == 0) return;

        var channel = Channel.CreateBounded<(Guid Id, string Host)>(new BoundedChannelOptions(1024)
        {
            SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait,
        });
        var producer = Task.Run(async () =>
        {
            foreach (var t in targets) await channel.Writer.WriteAsync(t, stop);
            channel.Writer.Complete();
        }, stop);

        using var gate = new SemaphoreSlim(settings.MaxConcurrency);
        var inFlight = new List<Task>(targets.Count);
        await foreach (var target in channel.Reader.ReadAllAsync(stop))
        {
            await gate.WaitAsync(stop);
            inFlight.Add(Task.Run(async () =>
            {
                try { await PollOneAsync(target.Id, target.Host, settings, stop); }
                finally { gate.Release(); }
            }, stop));
        }
        await producer;
        await Task.WhenAll(inFlight);
        await RecordFleetAndPurgeAsync(settings, stop);
    }

    private DateTime _lastPurge = DateTime.MinValue;

    /// <summary>One fleet-wide average per cycle for the dashboard trend, plus an hourly purge of old history.</summary>
    private async Task RecordFleetAndPurgeAsync(PlatformSettings settings, CancellationToken stop)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            var now = clock.UtcNow;

            var online = await db.Endpoints.AsNoTracking().Where(e => e.Online)
                .Select(e => new { e.CpuPercent, e.RamPercent }).ToListAsync(stop);
            var total = await db.Endpoints.CountAsync(stop);
            db.FleetSamples.Add(new FleetSample
            {
                TimestampUtc = now, Total = total, Online = online.Count,
                AvgCpu = online.Any(o => o.CpuPercent.HasValue) ? Math.Round(online.Where(o => o.CpuPercent.HasValue).Average(o => o.CpuPercent!.Value), 1) : null,
                AvgRam = online.Any(o => o.RamPercent.HasValue) ? Math.Round(online.Where(o => o.RamPercent.HasValue).Average(o => o.RamPercent!.Value), 1) : null,
            });
            await db.SaveChangesAsync(stop);

            if (now - _lastPurge > TimeSpan.FromHours(1))
            {
                _lastPurge = now;
                var cutoff = now.AddDays(-settings.MetricRetentionDays);
                await db.MetricSamples.Where(m => m.TimestampUtc < cutoff).ExecuteDeleteAsync(stop);
                await db.FleetSamples.Where(m => m.TimestampUtc < cutoff).ExecuteDeleteAsync(stop);
                await db.DiskTemps.Where(m => m.TimestampUtc < cutoff).ExecuteDeleteAsync(stop);
                await PurgeJobsAsync(db, now, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recording fleet history failed");
        }
    }

    /// <summary>Finished jobs older than the retention period go, with their targets and output. The audit log is never touched.</summary>
    public static async Task PurgeJobsAsync(INexoraDb db, DateTime now, int retentionDays, CancellationToken stop)
    {
        var cutoff = now.AddDays(-Math.Max(1, retentionDays));
        var old = db.Jobs.Where(j => j.FinishedUtc != null && j.FinishedUtc < cutoff
                                     && j.Status != JobState.Queued && j.Status != JobState.WaitingForHelper && j.Status != JobState.Running);
        var ids = await old.OrderBy(j => j.FinishedUtc).Select(j => j.Id).Take(2000).ToListAsync(stop); // oldest first, in a defined order
        if (ids.Count == 0) return;
        var targetIds = await db.JobTargets.Where(t => ids.Contains(t.JobId)).Select(t => t.Id).ToListAsync(stop);
        foreach (var chunk in targetIds.Chunk(500)) await DeleteAsync(db, db.JobLogChunks.Where(l => chunk.Contains(l.JobTargetId)), stop);
        await DeleteAsync(db, db.DiagnosisRuns.Where(r => ids.Contains(r.JobId)), stop); // a diagnosis goes with its job (90 days by default)
        await DeleteAsync(db, db.JobTargets.Where(t => ids.Contains(t.JobId)), stop);
        await DeleteAsync(db, db.Jobs.Where(j => ids.Contains(j.Id)), stop);
        await db.SaveChangesAsync(stop);
    }

    private static async Task DeleteAsync<T>(INexoraDb db, IQueryable<T> q, CancellationToken stop) where T : class
    {
        try { await q.ExecuteDeleteAsync(stop); }
        catch (InvalidOperationException) { ((DbContext)db).RemoveRange(await q.ToListAsync(stop)); } // no set-based deletes (the in-memory test provider)
    }

    private Task PurgeJobsAsync(INexoraDb db, DateTime now, CancellationToken stop) =>
        PurgeJobsAsync(db, now, options.Value.Jobs.RetentionDays, stop);

    private int _diskScansThisCycle;

    /// <summary>Drive health changes slowly, so it is read on its own (longer) schedule.</summary>
    private async Task CollectDisksIfDueAsync(
        IServiceProvider services, ManagedEndpoint e, PlatformSettings settings, DateTime now, CancellationToken stop)
    {
        if (e.LastDiskPollUtc is { } last && now - last < TimeSpan.FromMinutes(settings.DiskPollMinutes)) return;
        // A rescan-all or a scoring change makes every PC due at once; scan only so many per cycle and leave the rest due for the next ones.
        if (Interlocked.Increment(ref _diskScansThisCycle) > Math.Max(1, options.Value.Monitoring.MaxDiskScansPerCycle)) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.Monitoring.ConnectTimeoutSeconds + 30));
            var expectedHost=e.Hostname;
            var expectedVersion=e.RowVersion;
            var disks = await WithDeadline(collector.CollectDisksAsync(e.Hostname, timeout.Token), TimeSpan.FromSeconds(options.Value.Monitoring.ConnectTimeoutSeconds + 45), e.Hostname);
            if (!await services.GetRequiredService<IDiskHealthService>().StoreIfCurrentAsync(e.Id, expectedHost, expectedVersion, disks, stop))
                logger.LogInformation("Discarded late disk evidence after endpoint {EndpointId} changed", e.Id);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Not fatal, and never allowed to take the CPU/RAM result down with it: keep the previous snapshot, say why the scan failed
            // (shown on the PC's page), and try again after the normal interval rather than on every cycle.
            var reference=Guid.NewGuid().ToString("N");
            logger.LogWarning(ex, "Drive scan of {Host} failed. Reference {Reference}", e.Hostname,reference);
            var reason = $"Drive evidence unavailable. Check connectivity, permissions and collector health. Reference: {reference}";
            try { await services.GetRequiredService<IDiskHealthService>().MarkScanFailedAsync(e.Id, reason, stop); }
            catch (Exception inner) when (inner is not OperationCanceledException) { logger.LogError(inner, "Could not record the drive scan failure of {Host}", e.Hostname); }
            e.LastDiskPollUtc = now;
            e.DiskScanError = reason;
        }
    }

    /// <summary>
    /// A hard deadline per host. A WMI call cannot always be cancelled, so a slow PC is ABANDONED when time is up (its late result is discarded and
    /// its error observed) instead of holding the whole cycle.
    /// </summary>
    public static async Task<T> WithDeadline<T>(Task<T> work, TimeSpan limit, string what)
    {
        var done = await Task.WhenAny(work, Task.Delay(limit));
        if (done == work) return await work;
        _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // observed, so it cannot surface later as an unobserved exception
        throw new TimeoutException($"{what} did not answer within {limit.TotalSeconds:0} s.");
    }

    private async Task PollOneAsync(Guid id, string host, PlatformSettings settings, CancellationToken stop)
    {
        EndpointSample? sample = null;
        string? error = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.Monitoring.ConnectTimeoutSeconds + 15));
            sample = await WithDeadline(collector.CollectSampleAsync(host, timeout.Token), TimeSpan.FromSeconds(options.Value.Monitoring.ConnectTimeoutSeconds + 20), host);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            error = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
        }

        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, stop);
            if (e is null) return;
            // The inventory address may have changed while the remote read was in flight.
            // Never attach the old machine's sample (or failure) to the new target.
            if (!string.Equals(e.Hostname, host, StringComparison.OrdinalIgnoreCase)) return;

            var wasOnline = e.Online;
            var alertsBefore = Mapping.AlertsFor(e, settings);
            var now = clock.UtcNow;
            e.LastPolledUtc = now;

            if (sample is not null)
            {
                e.Online = true;
                e.ConsecutiveFailures = 0;
                e.LastError = null;
                e.LastSeenUtc = now;
                e.IpAddress = sample.IpAddress ?? e.IpAddress;
                e.LastUser = sample.LastUser;
                e.OsCaption = sample.OsCaption ?? e.OsCaption;
                e.OsVersion = sample.OsVersion ?? e.OsVersion;
                e.BootTimeUtc = sample.BootTimeUtc ?? e.BootTimeUtc;
                e.CpuPercent = sample.CpuPercent;
                e.RamPercent = sample.RamPercent;
                e.DiskPercent = sample.DiskPercent;
                e.RamTotalBytes = sample.RamTotalBytes ?? e.RamTotalBytes;
                e.RamFreeBytes = sample.RamFreeBytes;   // paired with RamPercent of the same read, so never kept from an older poll
                if (sample.Volumes is { } vols)
                    e.VolumesJson = System.Text.Json.JsonSerializer.Serialize(vols.Select(v => new VolumeDto(v.Letter, v.FileSystem, v.TotalBytes, v.FreeBytes)));
                e.ReportedHostname = sample.ReportedHostname ?? e.ReportedHostname;
                e.Manufacturer = sample.Manufacturer ?? e.Manufacturer;
                e.Model = sample.Model ?? e.Model;
                db.MetricSamples.Add(new MetricSample
                {
                    EndpointId = e.Id, TimestampUtc = now, Cpu = sample.CpuPercent, Ram = sample.RamPercent, Disk = sample.DiskPercent,
                });
            }
            else
            {
                e.ConsecutiveFailures++;
                e.LastError = error;
                if (e.ConsecutiveFailures >= settings.OfflineAfterFailures) e.Online = false;
            }
            await db.SaveChangesAsync(stop);

            // An unreachable PC cannot be scanned; trying anyway only adds a long timeout and a failure that says nothing new.
            if (sample is not null) await CollectDisksIfDueAsync(scope.ServiceProvider, e, settings, now, stop);

            await notifier.EndpointUpdatedAsync(await scope.ServiceProvider.GetRequiredService<IEndpointDtoFactory>().BuildOneAsync(e, stop));
            if (wasOnline != e.Online || !alertsBefore.SequenceEqual(Mapping.AlertsFor(e, settings)))
                await notifier.DashboardChangedAsync();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to store poll result for {Host}", host);
        }
    }
}
