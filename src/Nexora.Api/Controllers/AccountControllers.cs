using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nexora.Api.Auth;
using Nexora.Api.Http;
using Nexora.Application;
using Nexora.Application.Services;

namespace Nexora.Api.Controllers;

/// <summary>Endpoints a signed-in user can reach even without a Nexora role.</summary>
[ApiController]
[Authorize(Policy = Policies.Authenticated)]
public sealed class AccountController(
    IMeService me, IAccessRequestService access, IAccessGroupService group, IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet("api/me")]
    public async Task<ActionResult<MeDto>> GetMe(CancellationToken ct)
    {
        var sid = User.GetSid();
        if (sid is null) return Unauthorized();
        return await me.BuildAsync(sid, User.GetAccount(), group.IsMember(User), ct);
    }

    /// <summary>Called by the waiting page when /api/me reports NoAccess or Rejected.</summary>
    [HttpPost("api/access/request")]
    public async Task<IActionResult> RequestAccess(CancellationToken ct) =>
        this.ToAction(await access.RequestAsync(HttpContext.ToActor(), group.IsMember(User), ct));

    [HttpGet("api/antiforgery")]
    public IActionResult GetToken() => Ok(new { token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });
}

/// <summary>Role Management: SuperAdmin only. Everyone else gets 404 so the surface is not discoverable.</summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = Policies.SuperAdmin)]
[HideWhenForbidden]
public sealed class AdminController(IRoleManagementService roles) : ControllerBase
{
    public sealed record RoleInput(string Role);
    public sealed record RejectInput(string Reason);
    public sealed record AddUserInput(string Sid, string Role);

    [HttpGet("access-requests")]
    public async Task<IReadOnlyList<AccessRequestDto>> Requests([FromQuery] string? status, CancellationToken ct) =>
        await roles.ListRequestsAsync(status, ct);

    [HttpPost("access-requests/{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] RoleInput input, CancellationToken ct) =>
        this.ToAction(await roles.ApproveAsync(HttpContext.ToActor(), id, input.Role, ct));

    [HttpPost("access-requests/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectInput input, CancellationToken ct) =>
        this.ToAction(await roles.RejectAsync(HttpContext.ToActor(), id, input.Reason, ct));

    [HttpGet("users")]
    public async Task<IReadOnlyList<UserDto>> Users([FromQuery] string? search, [FromQuery] string? role, CancellationToken ct) =>
        await roles.ListUsersAsync(search, role, ct);

    [HttpPut("users/{sid}/role")]
    public async Task<IActionResult> ChangeRole(string sid, [FromBody] RoleInput input, CancellationToken ct) =>
        !SidRules.IsValid(sid) ? BadRequest(new { error = "Invalid SID." })
            : this.ToAction(await roles.ChangeRoleAsync(HttpContext.ToActor(), sid, input.Role, ct));

    [HttpPost("users/{sid}/revoke")]
    public async Task<IActionResult> Revoke(string sid, CancellationToken ct) =>
        !SidRules.IsValid(sid) ? BadRequest(new { error = "Invalid SID." })
            : this.ToAction(await roles.RevokeAsync(HttpContext.ToActor(), sid, ct));

    [HttpPost("users")]
    public async Task<IActionResult> AddUser([FromBody] AddUserInput input, CancellationToken ct) =>
        !SidRules.IsValid(input.Sid) ? BadRequest(new { error = "Invalid SID." })
            : this.ToAction(await roles.AddUserAsync(HttpContext.ToActor(), input.Sid, input.Role, ct));

    [HttpGet("directory")]
    public IReadOnlyList<DirectoryUserDto> Directory([FromQuery] string q) => roles.SearchDirectory(q);
}

[ApiController]
[Route("api/audit")]
[Authorize(Policy = Policies.Viewer)]
public sealed class AuditController(IAuditService audit) : ControllerBase
{
    /// <summary>Admins see everything; Viewers and Operators only their own entries.</summary>
    [HttpGet]
    public async Task<PagedResult<AuditEntryDto>> Get(
        [FromQuery] string? actorSid, [FromQuery] string? action, [FromQuery] string? text,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var canSeeAll = User.HasNexoraRole(Nexora.Domain.NexoraRoles.Admin, Nexora.Domain.NexoraRoles.SuperAdmin);
        var effectiveActor = canSeeAll ? actorSid : User.GetSid();
        return await audit.QueryAsync(new AuditQuery(effectiveActor, action, text, from?.ToUniversalTime(), to?.ToUniversalTime(), page, pageSize), ct);
    }
}

[ApiController]
[Route("api/settings")]
[Authorize(Policy = Policies.Admin)]
public sealed class SettingsController(ISettingsService settings, IOptions<FeatureOptions> features) : ControllerBase
{
    [HttpGet]
    public async Task<object> Get(CancellationToken ct) =>
        new { settings = await settings.GetAsync(ct), features = new { credentialOps = features.Value.CredentialOps } };

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] PlatformSettings input, CancellationToken ct) =>
        this.ToAction(await settings.UpdateAsync(HttpContext.ToActor(), input, ct));
}

[ApiController]
[Route("api/scripts")]
[Authorize(Policy = Policies.Operator)]
public sealed class ScriptsController(IScriptCatalogService scripts) : ControllerBase
{
    private bool IsAdmin => User.HasNexoraRole(Nexora.Domain.NexoraRoles.Admin, Nexora.Domain.NexoraRoles.SuperAdmin);

    /// <summary>Operators see only enabled scripts; Admins see all.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<ScriptDto>> List(CancellationToken ct) => await scripts.ListAsync(!IsAdmin, ct);

    [HttpPost, Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Create([FromBody] ScriptInput input, CancellationToken ct) =>
        this.ToAction(await scripts.CreateAsync(HttpContext.ToActor(), input, ct));

    [HttpPut("{id:guid}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Update(Guid id, [FromBody] ScriptInput input, CancellationToken ct) =>
        this.ToAction(await scripts.UpdateAsync(HttpContext.ToActor(), id, input, ct));

    [HttpDelete("{id:guid}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        this.ToAction(await scripts.DeleteAsync(HttpContext.ToActor(), id, ct));
}
