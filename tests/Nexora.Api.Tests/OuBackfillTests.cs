using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Api.Tests;

public sealed class OuBackfillTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private sealed class Clock : ISystemClock { public DateTime UtcNow { get; set; } = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc); }

    private sealed class Dir : IDirectoryLookup
    {
        public OuLookupResult Result = OuLookupResult.Ok(new Dictionary<string, string>());
        public int Calls;
        public string? GetDisplayName(string sid) => null;
        public string? ResolveGroupSid(string groupName) => null;
        public bool IsUserInGroup(string userSid, string groupNameOrSid) => false;
        public DirectoryUserDto? FindUserBySid(string sid) => null;
        public IReadOnlyList<DirectoryUserDto> SearchUsers(string query, int max) => [];
        public IReadOnlyList<DirectoryComputer> SearchComputers(string? namePrefix, int max) => [];
        public OuLookupResult ComputerOuMap() { Calls++; return Result; }
    }

    private OuBackfillService Svc(Dir dir, Clock clock) =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(), dir, clock, NullLogger<OuBackfillService>.Instance);

    private async Task<string> SeedAsync(Action<ManagedEndpoint>? configure = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        var e = new ManagedEndpoint { Id = Guid.NewGuid(), Hostname = "ou" + Guid.NewGuid().ToString("N")[..10], CreatedUtc = DateTime.UtcNow };
        configure?.Invoke(e);
        db.Endpoints.Add(e);
        await db.SaveChangesAsync();
        return e.Hostname;
    }

    private ManagedEndpoint Row(string host) => factory.Query(db => db.Endpoints.AsNoTracking().Single(e => e.Hostname == host));

    private static OuLookupResult Map(string host, string ou) => OuLookupResult.Ok(new Dictionary<string, string> { [host] = ou });

    [Fact]
    public async Task A_failed_lookup_marks_pending_rows_failed_without_stamping_them_checked_and_the_next_cycle_retries()
    {
        var host = await SeedAsync();
        var dir = new Dir { Result = OuLookupResult.Failed("Access to Active Directory was denied.") };
        var clock = new Clock();
        var svc = Svc(dir, clock);

        await svc.RunAsync(default);

        var failed = Row(host);
        failed.OuLookupStatus.Should().Be("failed");
        failed.OuLookupError.Should().Be("Access to Active Directory was denied.");
        failed.OuCheckedUtc.Should().BeNull();
        failed.OrganizationalUnit.Should().BeNull();

        dir.Result = Map(host, "HQ/IT");
        clock.UtcNow = clock.UtcNow.AddSeconds(61);
        await svc.RunAsync(default);

        var ok = Row(host);
        (ok.OrganizationalUnit, ok.OuLookupStatus, ok.OuLookupError).Should().Be(("HQ/IT", "found", null));
        ok.OuCheckedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task A_failed_refresh_does_not_clear_an_existing_OU()
    {
        var clock = new Clock();
        var host = await SeedAsync(e => { e.OrganizationalUnit = "HQ/IT"; e.OuLookupStatus = "found"; e.OuCheckedUtc = clock.UtcNow.AddDays(-2); });
        var dir = new Dir { Result = OuLookupResult.Failed("The Active Directory query timed out.") };

        await Svc(dir, clock).RunAsync(default);

        var row = Row(host);
        row.OrganizationalUnit.Should().Be("HQ/IT");
        row.OuLookupStatus.Should().Be("found");
        row.OuLookupError.Should().BeNull();
    }

    [Fact]
    public async Task A_found_OU_is_refreshed_after_24_hours_so_AD_moves_are_picked_up()
    {
        var clock = new Clock();
        var host = await SeedAsync(e => { e.OrganizationalUnit = "Old"; e.OuLookupStatus = "found"; e.OuCheckedUtc = clock.UtcNow.AddHours(-25); });

        await Svc(new Dir { Result = Map(host, "New/Place") }, clock).RunAsync(default);

        Row(host).OrganizationalUnit.Should().Be("New/Place");
    }

    [Fact]
    public async Task NotFound_is_retried_only_after_24_hours()
    {
        var host = await SeedAsync();
        var dir = new Dir();
        var clock = new Clock();
        var svc = Svc(dir, clock);

        await svc.RunAsync(default);
        Row(host).OuLookupStatus.Should().Be("notFound");
        Row(host).OuCheckedUtc.Should().Be(clock.UtcNow);
        var callsAfterFirst = dir.Calls;

        dir.Result = Map(host, "Appeared");
        clock.UtcNow = clock.UtcNow.AddHours(23);
        await svc.RunAsync(default);
        Row(host).OuLookupStatus.Should().Be("notFound", "not yet due");

        clock.UtcNow = clock.UtcNow.AddHours(2);
        await svc.RunAsync(default);
        Row(host).OrganizationalUnit.Should().Be("Appeared");
        dir.Calls.Should().BeGreaterThan(callsAfterFirst);
    }

    [Fact]
    public async Task Rows_added_right_after_a_run_are_picked_up_on_the_next_cycle_after_the_minimum_spacing_not_an_hour_later()
    {
        var dir = new Dir();
        var clock = new Clock();
        var svc = Svc(dir, clock);
        await SeedAsync();
        await svc.RunAsync(default);

        var later = await SeedAsync();
        dir.Result = Map(later, "Branches/X");
        clock.UtcNow = clock.UtcNow.AddSeconds(10);
        await svc.RunAsync(default);
        Row(later).OuLookupStatus.Should().BeNull("under the 60 s spacing between LDAP queries");

        clock.UtcNow = clock.UtcNow.AddSeconds(61);
        await svc.RunAsync(default);
        Row(later).OrganizationalUnit.Should().Be("Branches/X");
    }

    [Fact]
    public async Task The_endpoint_dto_exposes_ouStatus_and_ouError_and_hides_the_error_unless_failed()
    {
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var failedHost = await SeedAsync(e => { e.OuLookupStatus = "failed"; e.OuLookupError = "The Active Directory query failed."; });
        var foundHost = await SeedAsync(e => { e.OrganizationalUnit = "HQ/IT"; e.OuLookupStatus = "found"; e.OuLookupError = "stale"; });
        var legacyHost = await SeedAsync(e => e.OrganizationalUnit = "Legacy/OU");
        var neverHost = await SeedAsync();

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/endpoints");
        JsonElement Find(string host) => list.EnumerateArray().Single(x => x.GetProperty("hostname").GetString() == host);

        Find(failedHost).GetProperty("ouStatus").GetString().Should().Be("failed");
        Find(failedHost).GetProperty("ouError").GetString().Should().Be("The Active Directory query failed.");
        Find(foundHost).GetProperty("ouStatus").GetString().Should().Be("found");
        Find(foundHost).GetProperty("ouError").ValueKind.Should().Be(JsonValueKind.Null);
        Find(legacyHost).GetProperty("ouStatus").GetString().Should().Be("found");
        Find(neverHost).GetProperty("ouStatus").ValueKind.Should().Be(JsonValueKind.Null);
        Find(neverHost).GetProperty("organizationalUnit").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_catalog_dto_carries_category_keys()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var catalog = await op.GetFromJsonAsync<JsonElement>("/api/jobs/catalog");
        var cats = catalog.EnumerateArray().Select(a => a.GetProperty("category").GetString()).ToList();
        cats.Should().NotBeEmpty().And.OnlyContain(c => Nexora.Application.Jobs.ActionCategories.All.Contains(c!));
    }

    [PgFact]
    public async Task AddOuLookupState_is_additive_and_keeps_existing_rows()
    {
        using var pg = new IsolatedPostgres();
        await using var db = new NexoraDbContext(new DbContextOptionsBuilder<NexoraDbContext>().UseNpgsql(pg.ConnectionString).Options);
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate("20261014111639_UniqueLowerHostname");
        await using var c = new NpgsqlConnection(pg.ConnectionString);
        await c.OpenAsync();
        await using (var ins = new NpgsqlCommand(@"INSERT INTO endpoints (""Id"",""Hostname"",""Online"",""ConsecutiveFailures"",""CreatedUtc"",""OrganizationalUnit"") VALUES (@id,'PC-OLD',false,0,now(),'HQ/IT')", c))
        {
            ins.Parameters.AddWithValue("id", Guid.NewGuid());
            await ins.ExecuteNonQueryAsync();
        }

        migrator.Migrate();

        await using var q = new NpgsqlCommand(@"SELECT ""OrganizationalUnit"", ""OuLookupStatus"", ""OuLookupError"", (SELECT count(*) FROM endpoints) FROM endpoints", c);
        await using var r = await q.ExecuteReaderAsync();
        (await r.ReadAsync()).Should().BeTrue();
        r.GetString(0).Should().Be("HQ/IT");
        r.IsDBNull(1).Should().BeTrue();
        r.IsDBNull(2).Should().BeTrue();
        r.GetInt64(3).Should().Be(1);
    }
}
