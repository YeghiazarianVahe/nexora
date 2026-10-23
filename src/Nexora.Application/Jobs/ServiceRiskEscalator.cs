using Microsoft.Extensions.Logging;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Application.Jobs;

/// <summary>
/// Composes the catalog risk with what a service stop or restart would disturb. Start stays at its catalog level (Low), and stop and restart stay
/// Medium unless a RUNNING service depends on the one being stopped, in which case the request is High (reason and typed workstation name).
/// Fail safe: when the dependents cannot be read (workstation offline, read failed or timed out, or too many workstations to check) the answer is High.
/// It only ever raises the risk. The read goes through <see cref="IServiceReader"/>: bounded, with a deadline, read-only, with the server's identity.
/// </summary>
public sealed class ServiceRiskEscalator(IServiceReader reader, ILogger<ServiceRiskEscalator> logger) : IRiskEscalator
{
    public const int MaxLookupTargets = ServiceGuard.MaxDependentTargets;
    private readonly IRiskEscalator _base = new CatalogRiskEscalator();

    public async Task<RiskLevel> EffectiveRiskAsync(ActionDefinition def, IReadOnlyList<ManagedEndpoint> targets, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var level = await _base.EffectiveRiskAsync(def, targets, parameters, ct);
        // Start is Low for one workstation, but starting a service on several at once is a fleet change: Medium (confirmation token).
        if (def.Code == "svc.start" && targets.Count > 1 && level < RiskLevel.Medium) return RiskLevel.Medium;
        if (!ServiceRules.IsStopOrRestart(def.Code) || targets.Count == 0) return level;
        // Without a service name (a preflight that carries no parameters) there is nothing to look up.
        if (!parameters.TryGetValue("serviceName", out var name) || !ServiceNameRules.IsValid(name)) return level;

        var high = level > RiskLevel.High ? level : RiskLevel.High;
        if (targets.Count > MaxLookupTargets) return high;
        if (targets.Any(t => !t.Online)) return high;

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(ServiceGuard.OverallDeadline);
        ServiceRead<IReadOnlyList<DependentService>>[] reads;
        try { reads = await Task.WhenAll(targets.Select(t => reader.ReadDependentsAsync(t.Hostname, name, ServiceReader.DefaultMaxAge, overall.Token))); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return high; }   // out of time: fail safe
        if (reads.Any(r => !r.Ok))
        {
            logger.LogInformation("Dependents of {Service} could not be read on at least one target; treating the request as High risk", name);
            return high;
        }
        return reads.Any(r => r.Value!.Any(d => ServiceRules.IsActive(d.State))) ? high : level;
    }
}
