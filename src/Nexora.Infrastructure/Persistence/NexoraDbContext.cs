using Microsoft.EntityFrameworkCore;
using Nexora.Application;
using Nexora.Domain;

namespace Nexora.Infrastructure.Persistence;

public sealed class NexoraDbContext(DbContextOptions<NexoraDbContext> options) : DbContext(options), INexoraDb
{
    public DbSet<User> Users => Set<User>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<ManagedEndpoint> Endpoints => Set<ManagedEndpoint>();
    public DbSet<ScriptDefinition> Scripts => Set<ScriptDefinition>();
    public DbSet<ActionJob> ActionJobs => Set<ActionJob>();
    public DbSet<SettingsRow> Settings => Set<SettingsRow>();
    public DbSet<EndpointGroup> Groups => Set<EndpointGroup>();
    public DbSet<EndpointGroupMember> GroupMembers => Set<EndpointGroupMember>();
    public DbSet<EndpointInventory> EndpointInventories => Set<EndpointInventory>();
    public DbSet<MetricSample> MetricSamples => Set<MetricSample>();
    public DbSet<FleetSample> FleetSamples => Set<FleetSample>();
    public DbSet<DiskHealthSnapshot> DiskHealth => Set<DiskHealthSnapshot>();
    public DbSet<DiskTempSample> DiskTemps => Set<DiskTempSample>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobTarget> JobTargets => Set<JobTarget>();
    public DbSet<JobLogChunk> JobLogChunks => Set<JobLogChunk>();
    public DbSet<ProfileSize> ProfileSizes => Set<ProfileSize>();
    public DbSet<DiagnosisRun> DiagnosisRuns => Set<DiagnosisRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Sid);
            e.Property(x => x.Sid).HasMaxLength(184);
            e.Property(x => x.SamAccountName).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.LastAuthScheme).HasMaxLength(32);
        });

        b.Entity<AccessRequest>(e =>
        {
            e.ToTable("access_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.Sid).HasMaxLength(184);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.GrantedRole).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RejectReason).HasMaxLength(500);
            e.HasIndex(x => new { x.Sid, x.Status });
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_entries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.ActorSid).HasMaxLength(184);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.TargetType).HasMaxLength(32);
            e.Property(x => x.TargetId).HasMaxLength(256);
            e.Property(x => x.ClientIp).HasMaxLength(64);
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => x.ActorSid);
        });

        b.Entity<ManagedEndpoint>(e =>
        {
            e.ToTable("endpoints");
            // Read xmin for explicit refresh validation. Do not change concurrency semantics
            // of unrelated monitoring and endpoint-edit operations.
            e.Property(x => x.RowVersion).ValueGeneratedOnAddOrUpdate().HasColumnType("xid").HasColumnName("xmin");
            e.HasKey(x => x.Id);
            e.Property(x => x.Hostname).HasMaxLength(63);
            e.HasIndex(x => x.Hostname).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.Property(x => x.ReportedHostname).HasMaxLength(100);
            e.Property(x => x.Manufacturer).HasMaxLength(100);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.OrganizationalUnit).HasMaxLength(400);
            e.Property(x => x.OuLookupStatus).HasMaxLength(16);
            e.Property(x => x.OuLookupError).HasMaxLength(200);
        });

        b.Entity<ScriptDefinition>(e =>
        {
            e.ToTable("scripts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Sha256).HasMaxLength(64);
        });

        b.Entity<ActionJob>(e =>
        {
            e.ToTable("action_jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.OperatorSid);
            e.HasIndex(x => x.CreatedUtc);
        });

        b.Entity<EndpointGroup>(e =>
        {
            e.ToTable("endpoint_groups");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(60);
            e.Property(x => x.Description).HasMaxLength(300);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<EndpointGroupMember>(e =>
        {
            e.ToTable("endpoint_group_members");
            e.HasKey(x => new { x.GroupId, x.EndpointId });
            e.HasIndex(x => x.EndpointId);
        });

        b.Entity<MetricSample>(e =>
        {
            e.ToTable("metric_samples");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.EndpointId, x.TimestampUtc });
            e.HasIndex(x => x.TimestampUtc);
        });

        b.Entity<EndpointInventory>(e =>
        {
            e.ToTable("endpoint_inventories");
            e.HasKey(x => x.EndpointId);
            e.Property(x => x.Json).HasColumnType("jsonb");
            e.HasOne<ManagedEndpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<FleetSample>(e =>
        {
            e.ToTable("fleet_samples");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.TimestampUtc);
        });

        b.Entity<DiskHealthSnapshot>(e =>
        {
            e.ToTable("disk_health");
            e.HasKey(x => x.Id);
            e.Property(x => x.DiskKey).HasMaxLength(128);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Serial).HasMaxLength(100);
            e.Property(x => x.MediaType).HasMaxLength(16);
            e.Property(x => x.BusType).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Summary).HasMaxLength(2000);
            e.Property(x => x.InterfaceWarning).HasMaxLength(300);
            e.Property(x => x.TempThresholdSource).HasMaxLength(16);
            e.HasIndex(x => new { x.EndpointId, x.DiskKey }).IsUnique();
        });

        b.Entity<DiskTempSample>(e =>
        {
            e.ToTable("disk_temp_samples");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.DiskKey).HasMaxLength(128);
            e.HasIndex(x => new { x.EndpointId, x.DiskKey, x.TimestampUtc });
        });

        b.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.ActionCode).HasMaxLength(64);
            e.Property(x => x.InitiatorSid).HasMaxLength(184);
            e.Property(x => x.InitiatorAccount).HasMaxLength(256);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.ClientIp).HasMaxLength(64);
            e.HasIndex(x => x.CreatedUtc);
            e.HasIndex(x => new { x.InitiatorSid, x.CreatedUtc });
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.BatchId);
        });

        b.Entity<ProfileSize>(e =>
        {
            e.ToTable("profile_sizes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Sid).HasMaxLength(184);
            e.Property(x => x.MeasuredBySid).HasMaxLength(184);
            e.HasIndex(x => new { x.EndpointId, x.Sid }).IsUnique();
            e.HasOne<ManagedEndpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DiagnosisRun>(e =>
        {
            e.ToTable("diagnosis_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.ProfileId).HasMaxLength(64);
            e.Property(x => x.InitiatedBySid).HasMaxLength(184);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.ResultJson).HasColumnType("jsonb");
            e.Property(x => x.EvidenceJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.EndpointId, x.StartedUtc });
            e.HasIndex(x => x.JobId);
            // One run in progress per workstation, enforced by the database (the service check alone can race).
            e.HasIndex(x => x.EndpointId).IsUnique().HasFilter("\"Status\" = 'Running'").HasDatabaseName("UX_diagnosis_runs_running");
            e.HasOne<ManagedEndpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<JobTarget>(e =>
        {
            e.ToTable("job_targets");
            e.HasKey(x => x.Id);
            e.Property(x => x.EndpointName).HasMaxLength(200);
            e.Property(x => x.Host).HasMaxLength(100);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ResultSummary).HasMaxLength(1000);
            e.Property(x => x.SkipReason).HasMaxLength(300);
            e.Property(x => x.OutputPath).HasMaxLength(500);
            e.Property(x => x.OutputSha256).HasMaxLength(64);
            e.HasIndex(x => x.JobId);
            e.HasIndex(x => new { x.EndpointId, x.Status });
        });

        b.Entity<JobLogChunk>(e =>
        {
            e.ToTable("job_log_chunks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Stream).HasMaxLength(10);
            e.HasIndex(x => new { x.JobTargetId, x.Seq }).IsUnique();
        });

        b.Entity<SettingsRow>(e =>
        {
            e.ToTable("settings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAuditLog();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardAuditLog();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>The audit log is append-only: refuse any update or delete before it reaches the database trigger.</summary>
    private void GuardAuditLog()
    {
        var tampered = ChangeTracker.Entries<AuditEntry>()
            .Any(e => e.State is EntityState.Modified or EntityState.Deleted);
        if (tampered)
            throw new InvalidOperationException("The audit log is append-only.");
    }
}
