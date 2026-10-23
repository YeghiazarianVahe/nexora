using Microsoft.EntityFrameworkCore;
using Nexora.Domain;

namespace Nexora.Application;

/// <summary>The data surface the use cases need. Implemented by the EF Core context in Infrastructure.</summary>
public interface INexoraDb
{
    DbSet<User> Users { get; }
    DbSet<AccessRequest> AccessRequests { get; }
    DbSet<AuditEntry> AuditEntries { get; }
    DbSet<ManagedEndpoint> Endpoints { get; }
    DbSet<ScriptDefinition> Scripts { get; }
    DbSet<ActionJob> ActionJobs { get; }
    DbSet<SettingsRow> Settings { get; }
    DbSet<EndpointGroup> Groups { get; }
    DbSet<EndpointGroupMember> GroupMembers { get; }
    DbSet<EndpointInventory> EndpointInventories { get; }
    DbSet<MetricSample> MetricSamples { get; }
    DbSet<FleetSample> FleetSamples { get; }
    DbSet<DiskHealthSnapshot> DiskHealth { get; }
    DbSet<DiskTempSample> DiskTemps { get; }
    DbSet<Job> Jobs { get; }
    DbSet<JobTarget> JobTargets { get; }
    DbSet<JobLogChunk> JobLogChunks { get; }
    DbSet<ProfileSize> ProfileSizes { get; }
    DbSet<DiagnosisRun> DiagnosisRuns { get; }
    /// <summary>Lets a use case discard half-saved state before it retries.</summary>
    Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker ChangeTracker { get; }
    Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>The authenticated operator performing a use case, as captured at the HTTP edge.</summary>
public sealed record Actor(string Sid, string Account, string? ClientIp);

public enum ErrorKind
{
    None,
    NotFound,
    Conflict,
    Validation,
    Forbidden,
    Unavailable,
    NotImplemented,
}

public sealed record Result(ErrorKind Kind = ErrorKind.None, string? Message = null)
{
    public bool Ok => Kind == ErrorKind.None;
    public static Result Success() => new();
    public static Result Fail(ErrorKind kind, string message) => new(kind, message);
}

public sealed record Result<T>(T? Value, ErrorKind Kind = ErrorKind.None, string? Message = null)
{
    public bool Ok => Kind == ErrorKind.None;
    public static Result<T> Success(T value) => new(value);
    public static Result<T> Fail(ErrorKind kind, string message) => new(default, kind, message);
}

/// <summary>Real-time pushes. Implemented with SignalR in Api; a no-op in unit tests.</summary>
public interface INexoraNotifier
{
    Task AccessRequestPendingAsync(AccessRequestDto request);
    Task AccessChangedAsync(string sid);
    Task EndpointUpdatedAsync(EndpointSummaryDto endpoint);
    Task DashboardChangedAsync();
}

public interface IUserAccessCache
{
    /// <summary>Cached (60 s) lookup of a user's active role.</summary>
    Task<CachedAccess> GetAsync(string sid, string? authScheme, CancellationToken ct = default);
    void Invalidate(string sid);
}

public sealed record CachedAccess(Role? Role, UserStatus? Status);

public interface IFeatureGate
{
    bool CredentialOpsEnabled { get; }
}

public interface ISystemClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : ISystemClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
