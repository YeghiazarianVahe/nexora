using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Auth;
using Nexora.Api.Http;
using Nexora.Application;
using Nexora.Application.Jobs;

namespace Nexora.Api.Controllers;

/// <summary>
/// Remote actions as jobs. Authorization is decided per action by the service (role, risk confirmation, protected
/// endpoints, parameters); this controller only maps the results to HTTP.
/// </summary>
[ApiController]
[Route("api/jobs")]
[Authorize(Policy = Policies.Viewer)]
public sealed class JobsController(IJobService jobs) : ControllerBase
{
    /// <summary>The action catalog, with <c>allowed</c> telling the UI which entries this user may run.</summary>
    [HttpGet("catalog")]
    public IReadOnlyList<CatalogEntry> Catalog() => jobs.Catalog(User.ToJobActor().Role);

    /// <summary>
    /// Step 1 of running a Medium or higher action: validates everything (and, for High/Critical, the typed
    /// confirmation) and returns a short-lived single-use token that <c>POST /api/jobs</c> requires.
    /// </summary>
    /// <summary>Before a bulk run: which targets are offline, protected or busy. Read-only; the real checks run again on submit.</summary>
    [HttpPost("preflight")]
    public async Task<IActionResult> Preflight([FromBody] PreflightRequest request, CancellationToken ct) =>
        this.ToAction(await jobs.PreflightAsync(HttpContext.ToJobActor(), request, ct));

    [HttpPost("confirm")]
    public async Task<IActionResult> Confirm([FromBody] ConfirmRequest request, CancellationToken ct) =>
        this.ToAction(await jobs.ConfirmAsync(HttpContext.ToJobActor(), request, ct));

    [HttpPost, EnableRateLimiting("jobs")]
    public async Task<IActionResult> Create([FromBody] CreateJobRequest request, CancellationToken ct) =>
        this.ToAction(await jobs.CreateAsync(HttpContext.ToJobActor(), request, ct), d => Accepted(d));

    [HttpGet]
    public async Task<PagedResult<JobSummary>> List(
        [FromQuery] string? status, [FromQuery] string? action, [FromQuery(Name = "operator")] string? initiator,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] Guid? endpointId = null, CancellationToken ct = default) =>
        await jobs.ListAsync(HttpContext.ToJobActor(), new JobListQuery(status, action, initiator, from?.ToUniversalTime(), to?.ToUniversalTime(), page, pageSize, endpointId), ct);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => this.ToAction(await jobs.GetAsync(HttpContext.ToJobActor(), id, ct));

    [HttpGet("{id:guid}/targets/{targetId:guid}/log")]
    public async Task<IActionResult> Log(Guid id, Guid targetId, [FromQuery] int after = 0, [FromQuery] int take = 500, CancellationToken ct = default) =>
        this.ToAction(await jobs.LogAsync(HttpContext.ToJobActor(), id, targetId, after, take, ct));

    [HttpGet("{id:guid}/targets/{targetId:guid}/output")]
    public async Task<IActionResult> Output(Guid id, Guid targetId, CancellationToken ct)
    {
        var result = await jobs.OutputTextAsync(HttpContext.ToJobActor(), id, targetId, ct);
        if (!result.Ok) return this.ToAction(result);
        return File(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(result.Value!)).ToArray(),
            "text/plain; charset=utf-8", $"nexora-job-{id.ToString()[..8]}-{targetId.ToString()[..8]}.log");
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) => this.ToAction(await jobs.CancelAsync(HttpContext.ToJobActor(), id, ct));
}
