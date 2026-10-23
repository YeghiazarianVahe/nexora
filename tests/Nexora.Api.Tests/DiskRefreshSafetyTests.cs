using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;
using Nexora.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Nexora.Api.Tests;

public sealed class DiskRefreshSafetyTests
{
    private sealed class RecordingLog : ILogger<DiskHealthService>
    {
        public Exception? Exception; public string? Message;
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull => null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
        { Exception=exception; Message=formatter(state,exception); }
    }
    private sealed class Clock : ISystemClock { public DateTime UtcNow => DateTime.UtcNow; }
    private sealed class Collector(Func<Task<IReadOnlyList<DiskRaw>>> collect) : IEndpointCollector
    {
        public Task<IReadOnlyList<DiskRaw>> CollectDisksAsync(string host, CancellationToken ct) => collect();
        public Task<EndpointSample> CollectSampleAsync(string host, CancellationToken ct) => throw new NotSupportedException();
        public Task<EndpointDetailDto> CollectDetailAsync(string host, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProfileInfo>> CollectProfilesAsync(string host, CancellationToken ct) => throw new NotSupportedException();
    }
    [PgFact]
    public async Task Late_results_are_rejected_after_readdress_ABA_or_recreation_and_do_not_reconcile_current_drives()
    {
        using var pg = new IsolatedPostgres();
        var options = new DbContextOptionsBuilder<NexoraDbContext>().UseNpgsql(pg.ConnectionString).Options;
        await using (var schema = new NexoraDbContext(options)) await schema.Database.EnsureCreatedAsync();
        foreach (var change in new[] { "address", "ABA", "delete", "recreate", "unchanged" })
        {
            var id = Guid.NewGuid(); var host = "OLD-" + id.ToString("N");
            await using (var seed = new NexoraDbContext(options))
            {
                seed.Endpoints.Add(new ManagedEndpoint { Id=id, Hostname=host, CreatedUtc=DateTime.UtcNow });
                seed.DiskHealth.Add(new DiskHealthSnapshot { Id=Guid.NewGuid(), EndpointId=id, DiskKey="keep", Model="Current drive", UpdatedUtc=DateTime.UtcNow });
                await seed.SaveChangesAsync();
            }
            await using var read = new NexoraDbContext(options);
            var collector = new Collector(async () =>
            {
                await using var writer = new NexoraDbContext(options);
                var row = await writer.Endpoints.SingleAsync(e => e.Id==id);
                if(change is "address" or "ABA") { row.Hostname="NEW-"+id.ToString("N"); await writer.SaveChangesAsync(); }
                if(change=="ABA") { row.Hostname=host; await writer.SaveChangesAsync(); }
                if(change is "delete" or "recreate") { writer.Endpoints.Remove(row); await writer.SaveChangesAsync(); }
                if(change=="recreate") { writer.Endpoints.Add(new ManagedEndpoint { Id=id, Hostname=host, CreatedUtc=DateTime.UtcNow }); await writer.SaveChangesAsync(); }
                return [];
            });
            var result = await new DiskHealthService(read,collector,new Clock()).RefreshAsync(id,null);
            result.Ok.Should().Be(change=="unchanged",change);
            if(change!="unchanged") result.Kind.Should().Be(ErrorKind.Conflict);
            if(change is "address" or "ABA") (await read.DiskHealth.AsNoTracking().SingleAsync(d=>d.EndpointId==id)).DiskKey.Should().Be("keep");
        }
    }
    [Fact]
    public async Task Collector_exception_is_sanitized_and_has_a_reference()
    {
        var options=new DbContextOptionsBuilder<NexoraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db=new NexoraDbContext(options);
        var id=Guid.NewGuid(); db.Endpoints.Add(new ManagedEndpoint { Id=id,Hostname="SECRET-HOST" }); await db.SaveChangesAsync();
        var collector=new Collector(()=>throw new IOException("password=secret \\\\sensitive-share RPC 10.99.99.99"));
        var log=new RecordingLog();
        var result=await new DiskHealthService(db,collector,new Clock(),logger:log).RefreshAsync(id,null);
        result.Kind.Should().Be(ErrorKind.Unavailable);
        result.Message.Should().Contain("Reference:").And.NotContain("SECRET-HOST").And.NotContain("password").And.NotContain("RPC").And.NotContain("sensitive-share");
        log.Exception!.Message.Should().Contain("password=secret");
        log.Message.Should().Contain(result.Message!.Split("Reference: ")[1]);
    }
}
