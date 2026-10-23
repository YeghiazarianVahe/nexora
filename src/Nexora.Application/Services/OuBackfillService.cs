using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nexora.Application.Services;

public static class OuStatuses
{
    public const string Found = "found";
    public const string NotFound = "notFound";
    public const string Failed = "failed";
}

/// <summary>
/// Resolves the AD organizational unit of workstations from one bulk LDAP query. Read-only. A failed query is recorded on the rows that
/// were waiting (status "failed") and retried on the next inventory cycle; it is never cached as "not found" and never clears a known OU.
/// Singleton: the only state is the time of the last LDAP query (instance field, used by the one monitoring loop).
/// </summary>
public sealed class OuBackfillService(IServiceScopeFactory scopes, IDirectoryLookup directory, ISystemClock clock, ILogger<OuBackfillService> logger)
{
    public static readonly TimeSpan MinQuerySpacing = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromHours(24);
    private const int MaxErrorLength = 200;

    private DateTime _lastQueryUtc = DateTime.MinValue;

    public async Task RunAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (now - _lastQueryUtc < MinQuerySpacing) return;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            var cutoff = now - RecheckAfter;
            var due = await db.Endpoints
                .Where(e => e.OuCheckedUtc == null || e.OuCheckedUtc < cutoff || e.OuLookupStatus == OuStatuses.Failed)
                .ToListAsync(ct);
            if (due.Count == 0) return;
            _lastQueryUtc = now;

            var result = await Task.Run(() => directory.ComputerOuMap(), ct);
            if (result.Status != OuLookupStatus.Ok)
            {
                var error = Truncate(result.Error ?? "The Active Directory query failed.");
                foreach (var e in due.Where(d => d.OrganizationalUnit is null))
                {
                    e.OuLookupStatus = OuStatuses.Failed;
                    e.OuLookupError = error;   // OuCheckedUtc is left alone, so the row stays due for the next cycle
                }
                await db.SaveChangesAsync(ct);
                logger.LogWarning("OU lookup failed ({Reason}); {Count} workstations will be retried", error, due.Count);
                return;
            }

            var index = OuIndex.From(result);
            var found = 0;
            foreach (var e in due)
            {
                var match = index.Resolve(e.Hostname, e.ReportedHostname, out var ou);
                if (match == OuMatch.FoundAmbiguous)
                    logger.LogWarning("More than one AD computer matches {Host}; using the first", e.Hostname);
                e.OuCheckedUtc = now;
                e.OuLookupError = null;
                if (match != OuMatch.NotFound && ou is not null)
                {
                    e.OrganizationalUnit = ou;
                    e.OuLookupStatus = OuStatuses.Found;
                    found++;
                }
                else if (e.OrganizationalUnit is not null) e.OuLookupStatus = OuStatuses.Found;   // keep what is known
                else e.OuLookupStatus = OuStatuses.NotFound;
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Resolved organizational units for {Found} of {Count} workstations", found, due.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OU backfill failed");
        }
    }

    private static string Truncate(string s) => s.Length > MaxErrorLength ? s[..MaxErrorLength] : s;
}
