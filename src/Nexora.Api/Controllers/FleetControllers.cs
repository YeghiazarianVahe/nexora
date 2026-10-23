using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Auth;
using Nexora.Api.Http;
using Nexora.Application;
using Nexora.Application.Services;

namespace Nexora.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = Policies.Viewer)]
public sealed class DashboardController(IDashboardService dashboard, IMetricsService metrics) : ControllerBase
{
    [HttpGet]
    public Task<DashboardDto> Get(CancellationToken ct) => dashboard.GetAsync(ct);

    /// <summary>Fleet-wide average CPU / RAM and online count over time. range: 1h, 6h, 24h, 7d, 30d.</summary>
    [HttpGet("history")]
    public Task<IReadOnlyList<FleetPoint>> History([FromQuery] string? range, CancellationToken ct) =>
        metrics.FleetHistoryAsync(range, ct);
}

/// <summary>The thresholds every screen colours CPU, RAM, disk space and drive health by. Readable by any role (Settings itself is Admin only).</summary>
[ApiController]
[Route("api/thresholds")]
[Authorize(Policy = Policies.Viewer)]
public sealed class ThresholdsController(ISettingsService settings) : ControllerBase
{
    [HttpGet]
    public async Task<ThresholdsDto> Get(CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        return new ThresholdsDto(s.CpuAlertPercent, s.RamAlertPercent, s.DiskAlertPercent, DiskStatus.OkFrom, DiskStatus.CriticalBelow,
            s.DiskTempHddWarnC, s.DiskTempHddCritC, s.DiskTempSsdWarnC, s.DiskTempSsdCritC, s.DiskTempNvmeWarnC, s.DiskTempNvmeCritC);
    }
}

[ApiController]
[Route("api/endpoints")]
[Authorize(Policy = Policies.Viewer)]
public sealed class EndpointsController(IEndpointService endpoints, IHardwareInventoryService hardware, IMetricsService metrics, IDiskHealthService disks, IEndpointExportService export, ISystemClock clock, Nexora.Application.Jobs.IConfirmTokens confirmTokens, IProfileQuery profiles) : ControllerBase
{
    public sealed record AddInput(List<EndpointInput>? Items, List<Guid>? GroupIds);
    public sealed record UpdateInput(string? Name, string? Address, List<Guid>? GroupIds);
    public sealed record ImportInput(string? Prefix);
    /// <summary>Exactly one of Ids or Filter. Sort is optional and applies to both.</summary>
    public sealed record ExportInput(List<Guid>? Ids, EndpointFilter? Filter, EndpointSort? Sort);
    /// <summary>Either a list of ids, or All = true. Never both, never neither.</summary>
    public sealed record RemoveInput(List<Guid>? Ids, bool All, int? ExpectedCount = null, string? ConfirmToken = null);
    public sealed record BulkGroupsInput(List<Guid>? Ids, List<Guid>? AddGroupIds, List<Guid>? RemoveGroupIds);

    [HttpGet]
    public async Task<IReadOnlyList<EndpointSummaryDto>> List(
        [FromQuery] string? search, [FromQuery] bool? online, [FromQuery] Guid? groupId, CancellationToken ct) =>
        await endpoints.ListAsync(search, online, groupId, ct);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        await endpoints.GetAsync(id, ct) is { } e ? Ok(e) : NotFound(new { error = "Endpoint not found." });

    /// <summary>Case-insensitive lookup by host name (Viewer and above).</summary>
    [HttpGet("by-host/{host}"), Authorize(Policy = Policies.Viewer)]
    public async Task<IActionResult> ByHost(string host, CancellationToken ct) =>
        this.ToAction(await endpoints.GetByHostAsync(host, ct));

    /// <summary>Live read-only detail (services, processes, disks, software, event errors), collected on demand.</summary>
    [HttpGet("{id:guid}/detail"), Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct) =>
        this.ToAction(await endpoints.GetDetailAsync(id, ct));

    /// <summary>OS, CPU, memory modules, board and BIOS inventory. Cached; collected on demand, never by the high-frequency poll. Serial numbers: Operator and above.</summary>
    [HttpGet("{id:guid}/hardware")]
    public async Task<IActionResult> Hardware(Guid id, CancellationToken ct) =>
        this.ToAction(await hardware.GetAsync(id, false, CanSeeSerials(), ct));

    /// <summary>Workstation Overview: last known inventory (flagged stale when the PC is offline) plus the latest live CPU/RAM. Read-only; collection is rate-limited like /hardware. Never carries serial numbers.</summary>
    [HttpGet("{id:guid}/overview"), Authorize(Policy = Policies.Viewer)]
    public async Task<IActionResult> Overview(Guid id, CancellationToken ct) =>
        this.ToAction(await hardware.GetOverviewAsync(id, ct));

    /// <summary>Collect the inventory now (rate-limited per workstation). Read-only on the PC.</summary>
    [HttpPost("{id:guid}/hardware/refresh"), Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> RefreshHardware(Guid id, CancellationToken ct) =>
        this.ToAction(await hardware.GetAsync(id, true, CanSeeSerials(), ct));

    private bool CanSeeSerials() => User.HasNexoraRole(Nexora.Domain.NexoraRoles.Operator, Nexora.Domain.NexoraRoles.Admin, Nexora.Domain.NexoraRoles.SuperAdmin);

    /// <summary>CPU / RAM / disk usage history for the charts. range: 1h, 6h, 24h, 7d, 30d.</summary>
    [HttpGet("{id:guid}/metrics")]
    public async Task<IActionResult> Metrics(Guid id, [FromQuery] string? range, CancellationToken ct) =>
        await endpoints.GetAsync(id, ct) is null ? NotFound(new { error = "Endpoint not found." }) : Ok(await metrics.EndpointHistoryAsync(id, range, ct));

    /// <summary>Drive health and temperature (latest snapshot plus temperature history).</summary>
    [HttpGet("{id:guid}/disks")]
    public async Task<IActionResult> Disks(Guid id, [FromQuery] string? range, CancellationToken ct) =>
        await endpoints.GetAsync(id, ct) is null ? NotFound(new { error = "Endpoint not found." }) : Ok(await disks.GetAsync(id, range, ct));

    /// <summary>Read the drives now instead of waiting for the next scheduled poll.</summary>
    [HttpPost("{id:guid}/disks/refresh"), Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> RefreshDisks(Guid id, [FromQuery] string? range, CancellationToken ct) =>
        this.ToAction(await disks.RefreshAsync(id, range, ct));

    /// <summary>
    /// Local user profiles on the PC (Operator and above: account names are personal data; a Viewer gets 403). System profiles are never returned.
    /// { items } when the PC was read; { unavailable: true, reason, items, lastKnownUtc } when it is offline or cannot be read (last known rows, never deletable).
    /// Sizes are the server's stored measurements; deletion is Admin only and lives in <see cref="ProfileBatchController"/> and the job API.
    /// </summary>
    [HttpGet("{id:guid}/profiles"), Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> Profiles(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return this.ToAction(await profiles.ListAsync(HttpContext.ToJobActor(), id, ct), o => o.Unavailable
            ? Ok(new { unavailable = true, reason = o.Reason, items = o.Items, lastKnownUtc = o.LastKnownUtc })
            : Ok(new { items = o.Items }));
    }

    /// <summary>
    /// CSV export of the selected workstations (ids) or of everything matching a filter, in the table's sort order.
    /// With ?preview=true it returns just the mode and row count (no file, no audit entry).
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] ExportInput input, [FromQuery] bool preview, CancellationToken ct)
    {
        var result = await export.PrepareAsync(HttpContext.ToActor(), new ExportRequest(input.Ids, input.Filter, input.Sort), preview, ct);
        if (!result.Ok) return this.ToAction(result);
        var data = result.Value!;
        if (preview) return Ok(new { mode = data.Mode, count = data.Rows.Count });

        return new CsvStreamResult(
            EndpointCsv.FileName(data.Mode, clock.UtcNow),
            (writer, token) => EndpointCsv.WriteAsync(writer, data.Rows, EndpointCsv.ExportZone, token));
    }

    [HttpPost, Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Add([FromBody] AddInput input, CancellationToken ct) =>
        this.ToAction(await endpoints.AddAsync(HttpContext.ToActor(), input.Items ?? [], input.GroupIds, ct));

    [HttpPut("{id:guid}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateInput input, CancellationToken ct) =>
        this.ToAction(await endpoints.UpdateAsync(HttpContext.ToActor(), id, input.Name, input.Address, input.GroupIds, ct));

    [HttpPost("import-ad"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Import([FromBody] ImportInput input, CancellationToken ct) =>
        this.ToAction(await endpoints.ImportFromDirectoryAsync(HttpContext.ToActor(), input.Prefix, ct));

    /// <summary>Bulk update: add the selected workstations to groups and/or remove them from groups.</summary>
    [HttpPost("bulk-groups"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> BulkGroups([FromBody] BulkGroupsInput input, CancellationToken ct) =>
        this.ToAction(await endpoints.BulkGroupsAsync(HttpContext.ToActor(), input.Ids ?? [], input.AddGroupIds, input.RemoveGroupIds, ct), n => Ok(new { updated = n }));

    /// <summary>Step one of delete-all: how many workstations it would remove right now, and a short-lived single-use token for exactly that number.</summary>
    [HttpPost("remove/preview"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> RemovePreview(CancellationToken ct)
    {
        var count = await endpoints.CountRemovableAsync(ct);
        var token = confirmTokens.Issue(HttpContext.ToActor().Sid, "endpoint.remove_all", CountHash(count), "", false, DateTime.UtcNow);
        return Ok(new { count, confirmToken = token.Token, expiresUtc = token.ExpiresUtc });
    }

    private static string CountHash(int count) => $"count:{count}";

    [HttpPost("remove"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> RemoveMany([FromBody] RemoveInput input, CancellationToken ct)
    {
        if (input.All == (input.Ids is { Count: > 0 }))
            return BadRequest(new { error = "Provide either a list of ids or all = true." });
        if (input.All)
        {
            if (input.ExpectedCount is not { } expected
                || !confirmTokens.Consume(input.ConfirmToken, HttpContext.ToActor().Sid, "endpoint.remove_all", CountHash(expected), "", false, DateTime.UtcNow))
                return BadRequest(new { error = "Confirm delete-all again: the confirmation is missing, used or expired." });
        }
        return this.ToAction(await endpoints.RemoveManyAsync(HttpContext.ToActor(), input.All ? null : input.Ids, input.All ? input.ExpectedCount : null, ct), n => Ok(new { removed = n }));
    }

    [HttpDelete("{id:guid}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct) =>
        this.ToAction(await endpoints.RemoveAsync(HttpContext.ToActor(), id, ct));
}

[ApiController]
[Route("api/groups")]
[Authorize(Policy = Policies.Viewer)]
public sealed class GroupsController(IGroupService groups) : ControllerBase
{
    public sealed record MembersInput(List<Guid>? EndpointIds);
    public sealed record MemberChangesInput(List<Guid>? Add, List<Guid>? Remove);

    [HttpGet]
    public async Task<IReadOnlyList<GroupDto>> List(CancellationToken ct) => await groups.ListAsync(ct);

    [HttpPost, Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Create([FromBody] GroupInput input, CancellationToken ct) =>
        this.ToAction(await groups.CreateAsync(HttpContext.ToActor(), input, ct));

    [HttpPut("{id:guid}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Update(Guid id, [FromBody] GroupInput input, CancellationToken ct) =>
        this.ToAction(await groups.UpdateAsync(HttpContext.ToActor(), id, input, ct));

    [HttpDelete("{id:guid}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        this.ToAction(await groups.DeleteAsync(HttpContext.ToActor(), id, ct));

    [HttpPut("{id:guid}/members"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> SetMembers(Guid id, [FromBody] MembersInput input, CancellationToken ct) =>
        input.EndpointIds is null
            ? BadRequest(new { error = "Provide endpointIds; use an empty array to clear the group." })
            : this.ToAction(await groups.SetMembersAsync(HttpContext.ToActor(), id, input.EndpointIds, ct));

    /// <summary>Delta update used by the editor: safe when another operator changed the group meanwhile.</summary>
    [HttpPost("{id:guid}/members/changes"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> ChangeMembers(Guid id, [FromBody] MemberChangesInput input, CancellationToken ct) =>
        this.ToAction(await groups.ChangeMembersAsync(HttpContext.ToActor(), id, input.Add ?? [], input.Remove ?? [], ct));
}
