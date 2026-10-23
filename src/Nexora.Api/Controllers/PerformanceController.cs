using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexora.Api.Auth;
using Nexora.Application;
using Nexora.Application.Services;

namespace Nexora.Api.Controllers;

[ApiController]
[Authorize(Policy = Policies.Viewer)]
public sealed class PerformanceController(INexoraDb db, LivePerformanceReader reader, ILogger<PerformanceController> logger) : ControllerBase
{
    [HttpGet("api/endpoints/{id:guid}/performance")]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var endpoint = await db.Endpoints.AsNoTracking().Where(e => e.Id == id).Select(e => new { e.Hostname }).FirstOrDefaultAsync(ct);
        if (endpoint is null) return NotFound(new { error = "Endpoint not found." });
        try
        {
            var sample = await reader.ReadAsync(endpoint.Hostname, ct);
            return Ok(new { endpointId = id, hostname = endpoint.Hostname, sample });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var reference=System.Diagnostics.Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            logger.LogWarning(ex,"Live performance unavailable for endpoint {EndpointId}, reference {Reference}",id,reference);
            return StatusCode(503, new { error = $"Live CPU and RAM are temporarily unavailable. Retrying shortly. Reference: {reference}" });
        }
    }
}
