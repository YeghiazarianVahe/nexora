using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Auth;
using Nexora.Api.Http;
using Nexora.Application.Jobs;

namespace Nexora.Api.Controllers;

/// <summary>
/// Deleting several user profiles of one workstation (Admin only, enforced in the service so refusals are audited). Step 1 (confirm) re-verifies every profile on the PC and returns a single-use token
/// bound to the workstation name, the sorted SIDs, the operator and the risk; step 2 needs that token, the typed workstation name and a reason, and creates
/// ONE profile.delete job per profile (each audited on its own, all sharing a batch id). The jobs run one at a time in the operator's own helper; a failed
/// profile does not stop the rest. Nothing is ever created by "confirm". State-changing, so the global antiforgery / custom-header check applies.
/// </summary>
[ApiController]
[Route("api/endpoints/{id:guid}/profiles")]
[Authorize(Policy = Policies.Operator)]   // the service refuses and audits anything below Admin (job.denied); Viewers never get this far
[EnableRateLimiting("profile-batch")]
public sealed class ProfileBatchController(IProfileBatchService batch) : ControllerBase
{
    /// <summary>{ token, expectedHostname, count, items: [{ sid, account, path, sizeBytes }] }, or 400 { error, reasons: [{ sid, reason }] } when any profile cannot be deleted.</summary>
    [HttpPost("delete-batch/confirm")]
    public async Task<IActionResult> Confirm(Guid id, [FromBody] ProfileBatchConfirmRequest request, CancellationToken ct) =>
        Map(await batch.ConfirmAsync(HttpContext.ToJobActor(), id, request, ct));

    /// <summary>{ batchId, jobIds[] } (jobIds in the order of the confirm response's items), or 400 { error, reasons } / 403 / 409.</summary>
    [HttpPost("delete-batch")]
    public async Task<IActionResult> Delete(Guid id, [FromBody] ProfileBatchDeleteRequest request, CancellationToken ct) =>
        Map(await batch.DeleteAsync(HttpContext.ToJobActor(), id, request, ct), created => Accepted(created));

    private IActionResult Map<T>(BatchResult<T> r, Func<T, IActionResult>? ok = null) where T : class =>
        r.Reasons.Count > 0
            ? BadRequest(new { error = r.Result.Message, reasons = r.Reasons })
            : this.ToAction(r.Result, ok);
}
