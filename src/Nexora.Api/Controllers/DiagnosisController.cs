using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Auth;
using Nexora.Api.Http;
using Nexora.Application.Diagnostics;

namespace Nexora.Api.Controllers;

/// <summary>
/// The only field a client may send. Anything else (exchangeHosts, collector ids or bodies, parameters) makes the request fail to bind (400):
/// the collector set comes from the profile and the Exchange hosts from the Admin setting, on the server.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DiagnoseRequest(string? ProfileId);

/// <summary>
/// Deterministic diagnosis (F5). Every action is Operator and above: the start executes read-only collectors on the PC through the operator's own
/// helper, and the findings and raw evidence contain file paths and add-in names, so a Viewer gets 403. Starting is a state-changing POST, so the
/// global anti-forgery / Origin check applies; it creates a normal diag.collect job (denials and the run are audited by the job service). The
/// diagnosis only suggests actions; it never launches one.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.Operator)]
public sealed class DiagnosisController(IDiagnosisService diagnosis, IDiagnosticJobStarter starter) : ControllerBase
{
    [HttpGet("api/diagnose/profiles")]
    public IReadOnlyList<DiagProfileInfo> Profiles() => diagnosis.Profiles();

    [HttpGet("api/diagnose/runs/{id:guid}")]
    public async Task<IActionResult> Run(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return this.ToAction(await diagnosis.GetRunAsync(id, ct));
    }

    /// <summary>The raw collector outputs of the run: <c>{ "collector-id": { ... } }</c>.</summary>
    [HttpGet("api/diagnose/runs/{id:guid}/evidence")]
    public async Task<IActionResult> Evidence(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return this.ToAction(await diagnosis.EvidenceAsync(HttpContext.ToActor(), id, ct));
    }

    [HttpGet("api/endpoints/{id:guid}/diagnose/runs")]
    public async Task<IActionResult> History(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return this.ToAction(await diagnosis.HistoryAsync(id, ct));
    }

    /// <summary>202 { runId, jobId }; 501 { error } while Features:Diagnostics is off; 400/403/409 as for any refused job.</summary>
    [HttpPost("api/endpoints/{id:guid}/diagnose"), EnableRateLimiting("jobs")]
    public async Task<IActionResult> Start(Guid id, [FromBody] DiagnoseRequest request, CancellationToken ct) =>
        this.ToAction(await starter.StartDiagnosisAsync(HttpContext.ToJobActor(), id, request.ProfileId, ct), started => Accepted(started));
}
