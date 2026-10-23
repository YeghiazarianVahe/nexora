using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Auth;
using Nexora.Api.Http;
using Nexora.Application.Services;

namespace Nexora.Api.Controllers;

/// <summary>
/// Services of one workstation and what stopping one would disturb. Read-only (GET): starting, stopping and restarting are catalog jobs that run in
/// the operator's own helper. Operator and above, because the reads query the PC with the server's identity and are bounded and rate-shaped
/// in <see cref="IServiceReader"/>; a Viewer gets 403. A PC that cannot be read is a typed 200 envelope, never an error.
/// </summary>
[ApiController]
[Route("api/endpoints/{id:guid}/services")]
[Authorize(Policy = Policies.Operator)]
public sealed class WorkstationServicesController(IServiceQuery services) : ControllerBase
{
    /// <summary>An array of ServiceRowDto, or { unavailable: true, reason, items, lastKnownUtc } when the PC is offline or cannot be read.</summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return this.ToAction(await services.ListAsync(id, ct), o => o.Rows is not null
            ? Ok(o.Rows)
            : Ok(new { unavailable = true, reason = o.UnavailableReason, items = o.LastKnown ?? [], lastKnownUtc = o.LastKnownUtc }));
    }

    /// <summary>{ running, all, anyProtected, effectiveRisk }, or { unavailable: true, reason, ..., effectiveRisk: "High" } (unknown dependents are treated as High).</summary>
    [HttpGet("{name}/dependents")]
    public async Task<IActionResult> Dependents(Guid id, string name, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return this.ToAction(await services.DependentsAsync(id, name, ct), o => o.Dependents is not null
            ? Ok(o.Dependents)
            : Ok(new { unavailable = true, reason = o.UnavailableReason, running = Array.Empty<string>(), all = Array.Empty<string>(), anyProtected = false, effectiveRisk = ServiceRules.StopRisk(true) }));
    }
}
