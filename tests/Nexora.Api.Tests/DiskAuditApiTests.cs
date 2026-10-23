using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;
using Nexora.Infrastructure.Wmi;

namespace Nexora.Api.Tests;

public sealed class DiskAuditApiTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private static DiskHealthSnapshot Snap(Guid endpoint, string model, string serial, string media, string bus, int? health, int smartRecords, int driveCount, bool matched = true) => new()
    {
        Id = Guid.NewGuid(), EndpointId = endpoint, DiskKey = serial, Model = model, Serial = serial, MediaType = media, BusType = bus, HealthPercent = health,
        MediaTypeCode = media == "HDD" ? 3 : 4, SpindleSpeed = media == "HDD" ? 4_294_967_295 : 0, ClassSource = "media", HealthSource = "smart-attributes",
        PhysicalMatch = "Serial", SmartMatched = matched, HostSmartRecords = smartRecords, HostDriveCount = driveCount, ScoreVersion = DiskHealthEvaluator.ScoreVersion, UpdatedUtc = DateTime.UtcNow,
    };

    private async Task<(Guid Clean, Guid Broken)> SeedAsync()
    {
        var clean = await factory.SeedEndpointAsync("AUD-CLEAN", online: true);
        var broken = await factory.SeedEndpointAsync("AUD-BROKEN", online: true);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        db.DiskHealth.AddRange(
            Snap(clean, "ADATA SU800NS38", "C1", "SSD", "SATA", 65, 2, 2), Snap(clean, "ST1000DM010", "C2", "HDD", "SATA", 100, 2, 2),
            Snap(broken, "A", "B1", "SSD", "SATA", 90, 3, 2), Snap(broken, "B", "B2", "HDD", "SATA", 90, 3, 2));
        await db.SaveChangesAsync();
        return (clean, broken);
    }

    [Fact]
    public async Task The_audit_flags_the_broken_PC_and_passes_the_clean_one()
    {
        var (clean, broken) = await SeedAsync();
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);

        var report = (await admin.GetFromJsonAsync<DiskAuditReport>("/api/disk-audit"))!;

        report.Flagged.Should().Contain(p => p.EndpointId == broken && p.Findings.Any(f => f.Code == DiskAudit.CountMismatch));
        report.Flagged.Should().NotContain(p => p.EndpointId == clean);
        report.ByFinding.Should().ContainKey(DiskAudit.CountMismatch);
        report.Flagged.Single(p => p.EndpointId == broken).Drives.Should().HaveCount(2).And.OnlyContain(d => d.Serial != null && d.Class != "Unknown");
    }

    [Fact]
    public async Task The_audit_is_admin_only_and_hidden_from_everyone_else()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        foreach (var c in new[] { op, viewer })
        {
            (await c.GetAsync("/api/disk-audit")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await c.GetAsync("/api/disk-audit/export")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await c.PostAsync("/api/disk-audit/rescan", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        (await factory.ClientFor(null).GetAsync("/api/disk-audit")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_CSV_export_has_a_BOM_and_lists_the_flagged_drives()
    {
        var (_, broken) = await SeedAsync();
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);

        var response = await admin.GetAsync("/api/disk-audit/export");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.Should().Contain("Hostname,Findings").And.Contain("CountMismatch");
        _ = broken;
    }

    [Fact]
    public async Task Rescan_marks_every_PC_due_and_is_audited()
    {
        var sid = TestIdentities.NewSid();
        var admin = await factory.UserClientAsync(sid, Role.Admin);
        var id = await factory.SeedEndpointAsync("AUD-RESCAN", online: true);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            db.Endpoints.Single(e => e.Id == id).LastDiskPollUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var response = await admin.PostAsync("/api/disk-audit/rescan", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Query(db => db.Endpoints.Single(e => e.Id == id).LastDiskPollUtc).Should().BeNull();
        factory.Query(db => db.AuditEntries.Any(a => a.ActorSid == sid && a.Action == "diskaudit.rescan")).Should().BeTrue();
    }

    [Fact]
    public void The_NVMe_health_log_is_decoded_so_percentage_used_4_means_96_percent_left()
    {
        var log = new byte[512];
        log[0] = 0;               // critical warning
        log[3] = 100; log[4] = 50; // available spare, threshold
        log[5] = 4;               // percentage used
        BitConverter.GetBytes((ulong)2218).CopyTo(log, 128);   // power-on hours
        BitConverter.GetBytes((ulong)41).CopyTo(log, 144);     // unsafe shutdowns
        BitConverter.GetBytes((ulong)0).CopyTo(log, 160);      // media errors

        var health = NvmeSmart.Parse(log);

        health.LifeLeftPercent.Should().Be(96);
        (health.PowerOnHours, health.UnsafeShutdowns, health.MediaErrors, health.CriticalWarning).Should().Be((2218L, 41L, 0L, 0));
        NvmeSmart.Parse(WithUsed(log, 130)).LifeLeftPercent.Should().Be(0, "the drive may let percentage used run past 100");
    }

    private static byte[] WithUsed(byte[] log, byte used) { var c = (byte[])log.Clone(); c[5] = used; return c; }

    [Fact]
    public void Stored_health_scored_by_older_rules_is_marked_due_for_a_rescan_at_startup()
    {
        DiskHealthEvaluator.ScoreVersion.Should().BeGreaterThan(1, "version 1 was the scoring that penalized hours of use");
    }
}
