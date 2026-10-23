using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Api.Tests;

/// <summary>AddDiagnosisRuns on a real PostgreSQL (isolated cluster, skipped when no binaries are found): additive, keeps every row, cascades with the endpoint.</summary>
public sealed class DiagnosisMigrationTests
{
    private const string Before = "20261014220833_DriveHealthV4";

    private static async Task Exec(NpgsqlConnection c, string sql, params (string, object)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Count(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<HashSet<string>> Columns(NpgsqlConnection c)
    {
        var set = new HashSet<string>();
        await using var cmd = new NpgsqlCommand(@"SELECT table_name || '.' || column_name FROM information_schema.columns WHERE table_schema='public'", c);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetString(0));
        return set;
    }

    [PgFact]
    public async Task AddDiagnosisRuns_is_additive_keeps_existing_rows_and_cascades_with_the_endpoint()
    {
        using var pg = new IsolatedPostgres();
        await using var db = new NexoraDbContext(new DbContextOptionsBuilder<NexoraDbContext>().UseNpgsql(pg.ConnectionString).Options);
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate(Before);
        await using var c = new NpgsqlConnection(pg.ConnectionString);
        await c.OpenAsync();
        var keep = Guid.NewGuid();
        var job = Guid.NewGuid();
        await Exec(c, @"INSERT INTO endpoints (""Id"",""Hostname"",""Online"",""ConsecutiveFailures"",""CreatedUtc"") VALUES (@id,'PC-KEEP',true,0,now())", ("id", keep));
        await Exec(c, @"INSERT INTO endpoints (""Id"",""Hostname"",""Online"",""ConsecutiveFailures"",""CreatedUtc"") VALUES (@id,'PC-OTHER',false,0,now())", ("id", Guid.NewGuid()));
        await Exec(c, @"INSERT INTO jobs (""Id"",""ActionCode"",""ParametersJson"",""InitiatorSid"",""InitiatorAccount"",""CreatedUtc"",""Status"",""ProtectedOverride"",""TargetCount"") VALUES (@id,'demo.simulate','{}','S-1','T',now(),'Succeeded',false,1)", ("id", job));
        var before = await Columns(c);

        migrator.Migrate();

        var after = await Columns(c);
        before.Except(after).Should().BeEmpty("the migration only adds");
        after.Except(before).Should().BeEquivalentTo(
            new[] { "Id", "EndpointId", "JobId", "ProfileId", "ProfileVersion", "InitiatedBySid", "Status", "Complete", "StartedUtc", "FinishedUtc", "ResultJson", "EvidenceJson", "SchemaVersion" }
                .Select(n => "diagnosis_runs." + n));
        (await Count(c, "SELECT count(*) FROM endpoints")).Should().Be(2);
        (await Count(c, "SELECT count(*) FROM jobs")).Should().Be(1);
        await using (var t = new NpgsqlCommand(@"SELECT string_agg(column_name || ':' || data_type, ',' ORDER BY column_name) FROM information_schema.columns WHERE table_name='diagnosis_runs' AND column_name IN ('ResultJson','EvidenceJson')", c))
            ((string)(await t.ExecuteScalarAsync())!).Should().Be("EvidenceJson:jsonb,ResultJson:jsonb");

        var run = Guid.NewGuid();
        await Exec(c, @"INSERT INTO diagnosis_runs (""Id"",""EndpointId"",""JobId"",""ProfileId"",""ProfileVersion"",""InitiatedBySid"",""Status"",""Complete"",""StartedUtc"",""ResultJson"",""EvidenceJson"",""SchemaVersion"")
                        VALUES (@id,@e,@j,'pc-slow',1,'S-1','Complete',true,now(),'{""findings"":[]}'::jsonb,'{""a"":{}}'::jsonb,1)", ("id", run), ("e", keep), ("j", job));
        var orphan = async () => await Exec(c, @"INSERT INTO diagnosis_runs (""Id"",""EndpointId"",""JobId"",""ProfileId"",""ProfileVersion"",""InitiatedBySid"",""Status"",""Complete"",""StartedUtc"",""SchemaVersion"") VALUES (@id,@e,@j,'pc-slow',1,'S','Running',false,now(),1)",
            ("id", Guid.NewGuid()), ("e", Guid.NewGuid()), ("j", job));
        (await orphan.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503");

        await Exec(c, @"DELETE FROM endpoints WHERE ""Id""=@id", ("id", keep));
        (await Count(c, "SELECT count(*) FROM diagnosis_runs")).Should().Be(0, "a run is deleted with its endpoint");
        (await Count(c, "SELECT count(*) FROM jobs")).Should().Be(1, "the job row is not touched by the cascade");
    }

    [PgFact]
    public async Task Rolling_AddDiagnosisRuns_back_drops_only_its_own_table()
    {
        using var pg = new IsolatedPostgres();
        await using var db = new NexoraDbContext(new DbContextOptionsBuilder<NexoraDbContext>().UseNpgsql(pg.ConnectionString).Options);
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate();
        await using var c = new NpgsqlConnection(pg.ConnectionString);
        await c.OpenAsync();
        await Exec(c, @"INSERT INTO endpoints (""Id"",""Hostname"",""Online"",""ConsecutiveFailures"",""CreatedUtc"") VALUES (@id,'PC-KEEP',true,0,now())", ("id", Guid.NewGuid()));
        var full = await Columns(c);

        migrator.Migrate(Before);

        var down = await Columns(c);
        full.Except(down).Should().OnlyContain(x => x.StartsWith("diagnosis_runs."));
        down.Except(full).Should().BeEmpty();
        (await Count(c, "SELECT count(*) FROM endpoints")).Should().Be(1);
    }

    private sealed class NoEvents : Nexora.Application.Diagnostics.IDiagnosisEvents
    {
        public Task DiagnosisCompletedAsync(Guid jobId, Guid runId) => Task.CompletedTask;
    }

    [PgFact]
    public async Task A_collector_string_with_NUL_and_a_lone_surrogate_still_completes_and_stores_on_real_PostgreSQL()
    {
        using var pg = new IsolatedPostgres();
        await using var db = new NexoraDbContext(new DbContextOptionsBuilder<NexoraDbContext>().UseNpgsql(pg.ConnectionString).Options);
        db.Database.Migrate();
        var clock = new Nexora.Application.SystemClock();
        var profiles = Nexora.Application.Diagnostics.ProfileCatalog.FromAssembly(typeof(Nexora.Application.DependencyInjection).Assembly,
            Nexora.Application.Diagnostics.CollectorCatalog.FromAssembly(typeof(Nexora.Application.DependencyInjection).Assembly).Ids);
        var profile = profiles.Find("pc-slow")!;
        var ep = new Nexora.Domain.ManagedEndpoint { Id = Guid.NewGuid(), Hostname = "PC-NUL", CreatedUtc = DateTime.UtcNow };
        var job = new Nexora.Domain.Job { Id = Guid.NewGuid(), ActionCode = "diag.collect", InitiatorSid = "S-1", InitiatorAccount = "T", CreatedUtc = DateTime.UtcNow, Status = Nexora.Domain.JobState.Running, TargetCount = 1 };
        var target = new Nexora.Domain.JobTarget { Id = Guid.NewGuid(), JobId = job.Id, EndpointId = ep.Id, EndpointName = "PC-NUL", Host = "PC-NUL", Status = Nexora.Domain.TargetState.Succeeded };
        var run = new Nexora.Domain.DiagnosisRun { Id = Guid.NewGuid(), EndpointId = ep.Id, JobId = job.Id, ProfileId = profile.Id, ProfileVersion = 1, InitiatedBySid = "S-1", StartedUtc = DateTime.UtcNow };
        db.AddRange(ep, job, target, run);
        // As ConvertTo-Json would print them: escaped, so the line itself is plain text, but the JSON value holds U+0000 and a lone surrogate.
        const string procs = "{\"topCpu\":[{\"name\":\"bad\\u0000name\\ud800.exe\",\"pid\":1,\"cpuPercent\":80,\"workingSetBytes\":10}],\"topMemory\":[]}";
        db.JobLogChunks.Add(new Nexora.Domain.JobLogChunk { JobTargetId = target.Id, Seq = 1, TimestampUtc = DateTime.UtcNow, Stream = "stdout", Text = "##NXC pc-slow-processes " + procs });
        db.JobLogChunks.Add(new Nexora.Domain.JobLogChunk { JobTargetId = target.Id, Seq = 2, TimestampUtc = DateTime.UtcNow, Stream = "stdout", Text = "##NXC system-resources {\"cpu\":{\"loadPercent\":95}}" });
        await db.SaveChangesAsync();

        var completion = new Nexora.Application.Diagnostics.DiagnosisCompletion(db, profiles, new Nexora.Application.Diagnostics.RuleEngine(),
            new Nexora.Application.Services.AuditService(db, clock), new NoEvents(), clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<Nexora.Application.Diagnostics.DiagnosisCompletion>.Instance);
        await completion.OnTargetFinishedAsync(job, target, Nexora.Domain.TargetState.Succeeded);

        db.ChangeTracker.Clear();
        var stored = await db.DiagnosisRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        stored.Status.Should().Be("Incomplete", "two of four collectors delivered; the odd characters did not break the save");
        stored.EvidenceJson.Should().NotBeNull().And.Contain("bad").And.NotContain("\u0000");
        stored.ResultJson.Should().Contain("cpu-pressure");
        (await db.AuditEntries.CountAsync(a => a.Action == "diag.complete")).Should().Be(1);
    }
}
