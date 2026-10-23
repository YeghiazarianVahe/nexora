using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Api.Tests;

/// <summary>Runs only when PostgreSQL binaries are found (NEXORA_PG_BIN or the default install). Starts a throwaway cluster on a loopback port in a temp directory.</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (IsolatedPostgres.FindBin() is null) Skip = "PostgreSQL binaries not found (set NEXORA_PG_BIN); NOT VERIFIED on this machine."; }
}

internal sealed class IsolatedPostgres : IDisposable
{
    private readonly string _bin, _dir;
    public int Port { get; }
    public string ConnectionString => $"Host=127.0.0.1;Port={Port};Database=postgres;Username=postgres;Timeout=15";

    public static string? FindBin()
    {
        var candidates = new[] { Environment.GetEnvironmentVariable("NEXORA_PG_BIN"), @"C:\Program Files\PostgreSQL\17\bin", @"C:\Program Files\PostgreSQL\16\bin" };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, "pg_ctl.exe")));
    }

    private static void Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync(); var @out = p.StandardOutput.ReadToEndAsync();
        p.WaitForExit(120_000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed: {err.Result} {@out.Result}");
    }

    public IsolatedPostgres()
    {
        _bin = FindBin()!;
        _dir = Path.Combine(Path.GetTempPath(), "nexora-pgtest-" + Guid.NewGuid().ToString("N"));
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); Port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        Run(Path.Combine(_bin, "initdb.exe"), "-D", _dir, "-U", "postgres", "-A", "trust", "-E", "UTF8");
        Run(Path.Combine(_bin, "pg_ctl.exe"), "-D", _dir, "-w", "-l", Path.Combine(_dir, "log.txt"), "-o", $"-p {Port} -c listen_addresses=127.0.0.1", "start");
    }

    public void Dispose()
    {
        try { Run(Path.Combine(_bin, "pg_ctl.exe"), "-D", _dir, "-m", "immediate", "stop"); } catch { /* best effort */ }
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}

public sealed class IsolatedPostgresMigrationTests
{
    private const string Initial = "20261012090654_Initial";

    private static async Task Exec(NpgsqlConnection c, string sql, params (string, object)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    [PgFact]
    public async Task Upgrade_from_the_initial_schema_preserves_legacy_groups_and_memberships()
    {
        using var pg = new IsolatedPostgres();
        var cs = pg.ConnectionString;
        var options = new DbContextOptionsBuilder<NexoraDbContext>().UseNpgsql(cs).Options;
        await using var db = new NexoraDbContext(options);
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate(Initial);

        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var names = new string?[] { "Finance", "  finance ", "HQ Floor 2", null, "   ", new string('x', 90) };
        await using (var c = new NpgsqlConnection(cs))
        {
            await c.OpenAsync();
            for (var i = 0; i < ids.Length; i++)
                await Exec(c, @"INSERT INTO endpoints (""Id"",""Hostname"",""Online"",""ConsecutiveFailures"",""CreatedUtc"",""GroupName"") VALUES (@id,@h,false,0,now(),@g)",
                    ("id", ids[i]), ("h", "PC-" + i), ("g", (object?)names[i] ?? DBNull.Value));
        }

        migrator.Migrate();   // GroupsMetricsDisks and everything after it

        await using var v = new NpgsqlConnection(cs);
        await v.OpenAsync();
        var groups = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new NpgsqlCommand(@"SELECT ""Id"",""Name"",""Description"" FROM endpoint_groups", v))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) groups[r.GetString(1)] = r.GetGuid(0);
        groups.Keys.Select(k => k.ToLowerInvariant()).OrderBy(k => k, StringComparer.Ordinal).Should().Equal(new[] { "finance", "hq floor 2", new string('x', 60) }.OrderBy(k => k, StringComparer.Ordinal));

        var members = new List<(Guid G, Guid E)>();
        await using (var cmd = new NpgsqlCommand(@"SELECT ""GroupId"",""EndpointId"" FROM endpoint_group_members", v))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) members.Add((r.GetGuid(0), r.GetGuid(1)));
        members.Should().BeEquivalentTo(new[] { (groups["Finance"], ids[0]), (groups["Finance"], ids[1]), (groups["HQ Floor 2"], ids[2]), (groups[new string('x', 60)], ids[5]) });

        // the legacy column is gone and no endpoint row was lost
        await using var count = new NpgsqlCommand(@"SELECT (SELECT count(*) FROM endpoints), (SELECT count(*) FROM information_schema.columns WHERE table_name='endpoints' AND column_name='GroupName')", v);
        await using var rd = await count.ExecuteReaderAsync();
        await rd.ReadAsync();
        rd.GetInt64(0).Should().Be(6);
        rd.GetInt64(1).Should().Be(0);
    }

    private const string BeforeUniqueLower = "20261013101901_DiskScanError";

    private static Task InsertEndpoint(NpgsqlConnection c, string host) =>
        Exec(c, @"INSERT INTO endpoints (""Id"",""Hostname"",""Online"",""ConsecutiveFailures"",""CreatedUtc"") VALUES (@id,@h,false,0,now())", ("id", Guid.NewGuid()), ("h", host));

    [PgFact]
    public async Task UniqueLowerHostname_refuses_to_run_over_case_duplicates_and_changes_no_data()
    {
        using var pg = new IsolatedPostgres();
        await using var db = new NexoraDbContext(new DbContextOptionsBuilder<NexoraDbContext>().UseNpgsql(pg.ConnectionString).Options);
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate(BeforeUniqueLower);
        await using var c = new NpgsqlConnection(pg.ConnectionString);
        await c.OpenAsync();
        await InsertEndpoint(c, "PC-Dup"); await InsertEndpoint(c, "pc-dup"); await InsertEndpoint(c, "pc-ok");

        var act = () => migrator.Migrate();

        act.Should().Throw<Exception>().Where(e => e.ToString().Contains("PC-Dup") && e.ToString().Contains("pc-dup") && !e.ToString().Contains("pc-ok"));
        await using var count = new NpgsqlCommand("SELECT count(*) FROM endpoints", c);
        ((long)(await count.ExecuteScalarAsync())!).Should().Be(3, "nothing is deleted or merged");
        await using var idx = new NpgsqlCommand(@"SELECT count(*) FROM pg_indexes WHERE indexname='IX_endpoints_Hostname_lower'", c);
        ((long)(await idx.ExecuteScalarAsync())!).Should().Be(0);
    }

    [PgFact]
    public async Task UniqueLowerHostname_adds_the_index_when_there_are_no_duplicates()
    {
        using var pg = new IsolatedPostgres();
        await using var db = new NexoraDbContext(new DbContextOptionsBuilder<NexoraDbContext>().UseNpgsql(pg.ConnectionString).Options);
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate(BeforeUniqueLower);
        await using var c = new NpgsqlConnection(pg.ConnectionString);
        await c.OpenAsync();
        await InsertEndpoint(c, "PC-A"); await InsertEndpoint(c, "PC-B");

        migrator.Migrate();

        var dup = async () => await InsertEndpoint(c, "pc-a");
        (await dup.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
        await using var count = new NpgsqlCommand("SELECT count(*) FROM endpoints", c);
        ((long)(await count.ExecuteScalarAsync())!).Should().Be(2);
    }

    private const string BeforeEndpointInventory = "20261014115009_AddOuLookupState";

    [PgFact]
    public async Task AddEndpointInventory_is_additive_keeps_rows_and_cascades_with_the_endpoint()
    {
        using var pg = new IsolatedPostgres();
        await using var db = new NexoraDbContext(new DbContextOptionsBuilder<NexoraDbContext>().UseNpgsql(pg.ConnectionString).Options);
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate(BeforeEndpointInventory);
        await using var c = new NpgsqlConnection(pg.ConnectionString);
        await c.OpenAsync();
        var keep = Guid.NewGuid();
        await Exec(c, @"INSERT INTO endpoints (""Id"",""Hostname"",""Online"",""ConsecutiveFailures"",""CreatedUtc"") VALUES (@id,'PC-KEEP',true,0,now())", ("id", keep));
        await InsertEndpoint(c, "PC-OTHER");

        async Task<HashSet<string>> Columns()
        {
            var set = new HashSet<string>();
            await using var cmd = new NpgsqlCommand(@"SELECT table_name || '.' || column_name FROM information_schema.columns WHERE table_schema='public'", c);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) set.Add(r.GetString(0));
            return set;
        }
        var before = await Columns();

        migrator.Migrate();

        var after = await Columns();
        before.Except(after).Should().BeEmpty("the migration only adds");
        after.Except(before).Should().Contain("endpoint_inventories.Json").And.Contain("endpoints.RamFreeBytes");
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM endpoints", c))
            ((long)(await count.ExecuteScalarAsync())!).Should().Be(2);
        await using (var jt = new NpgsqlCommand(@"SELECT data_type FROM information_schema.columns WHERE table_name='endpoint_inventories' AND column_name='Json'", c))
            ((string)(await jt.ExecuteScalarAsync())!).Should().Be("jsonb");

        await Exec(c, @"INSERT INTO endpoint_inventories (""EndpointId"",""Json"",""CollectedUtc"") VALUES (@id,'{""Status"":""Complete""}'::jsonb,now())", ("id", keep));
        var orphan = async () => await Exec(c, @"INSERT INTO endpoint_inventories (""EndpointId"",""Json"",""CollectedUtc"") VALUES (@id,'{}'::jsonb,now())", ("id", Guid.NewGuid()));
        (await orphan.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503");

        await Exec(c, @"DELETE FROM endpoints WHERE ""Id""=@id", ("id", keep));
        await using var left = new NpgsqlCommand("SELECT count(*) FROM endpoint_inventories", c);
        ((long)(await left.ExecuteScalarAsync())!).Should().Be(0, "the snapshot is deleted with its endpoint");
    }

    private const string BeforeProfileSizes = "20261014151853_AddEndpointInventory";

    [PgFact]
    public async Task AddProfileSizeCache_is_additive_keeps_existing_rows_is_unique_per_profile_and_cascades_with_the_endpoint()
    {
        using var pg = new IsolatedPostgres();
        await using var db = new NexoraDbContext(new DbContextOptionsBuilder<NexoraDbContext>().UseNpgsql(pg.ConnectionString).Options);
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate(BeforeProfileSizes);
        await using var c = new NpgsqlConnection(pg.ConnectionString);
        await c.OpenAsync();
        var keep = Guid.NewGuid();
        var job = Guid.NewGuid();
        await Exec(c, @"INSERT INTO endpoints (""Id"",""Hostname"",""Online"",""ConsecutiveFailures"",""CreatedUtc"") VALUES (@id,'PC-KEEP',true,0,now())", ("id", keep));
        await Exec(c, @"INSERT INTO jobs (""Id"",""ActionCode"",""ParametersJson"",""InitiatorSid"",""InitiatorAccount"",""CreatedUtc"",""Status"",""ProtectedOverride"",""TargetCount"") VALUES (@id,'profile.sizes','{}','S-1-5-21-1-2-3-1000','TEST\op',now(),'Succeeded',false,1)", ("id", job));
        await InsertEndpoint(c, "PC-OTHER");

        async Task<HashSet<string>> Columns()
        {
            var set = new HashSet<string>();
            await using var cmd = new NpgsqlCommand(@"SELECT table_name || '.' || column_name || ':' || is_nullable FROM information_schema.columns WHERE table_schema='public'", c);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) set.Add(r.GetString(0));
            return set;
        }
        var before = await Columns();

        migrator.Migrate("20261014162035_AddProfileSizeCache");

        var after = await Columns();
        before.Except(after).Should().BeEmpty("the migration only adds: no column is dropped, renamed or made stricter");
        after.Except(before).Should().BeEquivalentTo(new[]
        {
            "jobs.BatchId:YES", "profile_sizes.Id:NO", "profile_sizes.EndpointId:NO", "profile_sizes.Sid:NO", "profile_sizes.SizeBytes:NO", "profile_sizes.MeasuredUtc:NO", "profile_sizes.JobId:NO", "profile_sizes.MeasuredBySid:NO",
        });
        await using (var count = new NpgsqlCommand(@"SELECT (SELECT count(*) FROM endpoints), (SELECT count(*) FROM jobs), (SELECT count(*) FROM jobs WHERE ""BatchId"" IS NULL)", c))
        await using (var rd = await count.ExecuteReaderAsync())
        {
            await rd.ReadAsync();
            (rd.GetInt64(0), rd.GetInt64(1), rd.GetInt64(2)).Should().Be((2L, 1L, 1L), "existing rows are kept and get no batch");
        }

        await Exec(c, @"INSERT INTO profile_sizes (""Id"",""EndpointId"",""Sid"",""SizeBytes"",""MeasuredUtc"",""JobId"",""MeasuredBySid"") VALUES (@id,@e,'S-1-5-21-1-2-3-4001',123,now(),@j,'S-1-5-21-1-2-3-1000')", ("id", Guid.NewGuid()), ("e", keep), ("j", job));
        var duplicate = async () => await Exec(c, @"INSERT INTO profile_sizes (""Id"",""EndpointId"",""Sid"",""SizeBytes"",""MeasuredUtc"",""JobId"",""MeasuredBySid"") VALUES (@id,@e,'S-1-5-21-1-2-3-4001',456,now(),@j,'S-1-5-21-1-2-3-1000')", ("id", Guid.NewGuid()), ("e", keep), ("j", job));
        (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505", "one row per (endpoint, SID)");
        var orphan = async () => await Exec(c, @"INSERT INTO profile_sizes (""Id"",""EndpointId"",""Sid"",""SizeBytes"",""MeasuredUtc"",""JobId"",""MeasuredBySid"") VALUES (@id,@e,'S-1-5-21-1-2-3-4002',1,now(),@j,'S-1-5-21-1-2-3-1000')", ("id", Guid.NewGuid()), ("e", Guid.NewGuid()), ("j", job));
        (await orphan.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503");

        await Exec(c, @"DELETE FROM endpoints WHERE ""Id""=@id", ("id", keep));
        await using var left = new NpgsqlCommand("SELECT count(*) FROM profile_sizes", c);
        ((long)(await left.ExecuteScalarAsync())!).Should().Be(0, "sizes are deleted with their endpoint");

        migrator.Migrate();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [PgFact]
    public async Task Upgrade_of_an_empty_database_still_works()
    {
        using var pg = new IsolatedPostgres();
        await using var db = new NexoraDbContext(new DbContextOptionsBuilder<NexoraDbContext>().UseNpgsql(pg.ConnectionString).Options);
        db.Database.Migrate();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }
}
