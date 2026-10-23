using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Nexora.Application;
using Nexora.Application.Jobs;
using Nexora.Domain;

namespace Nexora.Api.Auth;

public static class Policies
{
    public const string Authenticated = "Authenticated";
    public const string Viewer = "RequireViewer";
    public const string Operator = "RequireOperator";
    public const string Admin = "RequireAdmin";
    public const string SuperAdmin = "RequireSuperAdmin";

    public static void Register(AuthorizationOptions o)
    {
        o.AddPolicy(Authenticated, p => p.RequireAuthenticatedUser());
        o.AddPolicy(Viewer, p => p.RequireAuthenticatedUser().RequireRole(
            NexoraRoles.Viewer, NexoraRoles.Operator, NexoraRoles.Admin, NexoraRoles.SuperAdmin));
        o.AddPolicy(Operator, p => p.RequireAuthenticatedUser().RequireRole(
            NexoraRoles.Operator, NexoraRoles.Admin, NexoraRoles.SuperAdmin));
        o.AddPolicy(Admin, p => p.RequireAuthenticatedUser().RequireRole(NexoraRoles.Admin, NexoraRoles.SuperAdmin));
        o.AddPolicy(SuperAdmin, p => p.RequireAuthenticatedUser().RequireRole(NexoraRoles.SuperAdmin));

        // Anything not explicitly opened up requires a Nexora role. A user without a role reaches only
        // the endpoints marked [Authorize(Policy = Policies.Authenticated)] (me, access request, antiforgery, hub).
        o.FallbackPolicy = o.GetPolicy(Viewer);
    }
}

/// <summary>Marks an endpoint whose existence must not be revealed: a failed authorization answers 404, not 403.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class HideWhenForbiddenAttribute : Attribute;

public sealed class NexoraAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Forbidden && context.GetEndpoint()?.Metadata.GetMetadata<HideWhenForbiddenAttribute>() is not null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        return _default.HandleAsync(next, context, policy, result);
    }
}

public static class PrincipalExtensions
{
    /// <summary>The caller's SID. Identity is always the SID, never the display name.</summary>
    public static string? GetSid(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.PrimarySid)
        ?? (principal.Identity as System.Security.Principal.WindowsIdentity)?.User?.Value;

    public static string GetAccount(this ClaimsPrincipal principal) => principal.Identity?.Name ?? "";

    public static bool HasNexoraRole(this ClaimsPrincipal principal, params string[] roles) =>
        roles.Any(principal.IsInRole);

    /// <summary>The caller for the jobs module: Admin and SuperAdmin map to Role.Admin with IsAdmin set; no role gives null.</summary>
    public static JobActor ToJobActor(this ClaimsPrincipal principal, string? clientIp = null)
    {
        var role = principal.IsInRole(NexoraRoles.SuperAdmin) || principal.IsInRole(NexoraRoles.Admin) ? Role.Admin
            : principal.IsInRole(NexoraRoles.Operator) ? Role.Operator
            : principal.IsInRole(NexoraRoles.Viewer) ? Role.Viewer
            : (Role?)null;
        return new JobActor(new Actor(principal.GetSid() ?? "", principal.GetAccount(), clientIp), role, role == Role.Admin);
    }

    public static JobActor ToJobActor(this HttpContext context) =>
        context.User.ToJobActor(context.Connection.RemoteIpAddress?.ToString());

    public static Actor ToActor(this HttpContext context) =>
        new(context.User.GetSid() ?? "", context.User.GetAccount(), context.Connection.RemoteIpAddress?.ToString());
}
