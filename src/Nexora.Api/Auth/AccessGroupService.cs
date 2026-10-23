using System.Security.Claims;
using System.Security.Principal;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nexora.Application;

namespace Nexora.Api.Auth;

public interface IAccessGroupService
{
    /// <summary>True when the caller belongs to the configured access group ("IT Service Desk"), directly or nested.</summary>
    bool IsMember(ClaimsPrincipal principal);
}

/// <summary>
/// Two-step check. The caller's Windows token is consulted first (free, and covers security groups).
/// "IT Service Desk" is an Exchange distribution group, which never appears in a token, so a read-only
/// AD query (nested membership included) is the authoritative second step, cached for 5 minutes.
/// </summary>
public sealed class AccessGroupService(
    IOptions<NexoraOptions> options,
    IDirectoryLookup directory,
    IMemoryCache cache,
    ILogger<AccessGroupService> logger) : IAccessGroupService
{
    private static readonly TimeSpan MembershipTtl = TimeSpan.FromMinutes(5);

    public bool IsMember(ClaimsPrincipal principal)
    {
        var o = options.Value;
        var group = !string.IsNullOrWhiteSpace(o.AccessGroupSid) ? o.AccessGroupSid! : o.AccessGroup;

        if (TokenContains(principal, group, o)) return true;

        var sid = principal.GetSid();
        if (sid is null) return false;

        var key = $"member:{sid.ToUpperInvariant()}:{group}";
        if (cache.TryGetValue(key, out bool known)) return known;

        var member = directory.IsUserInGroup(sid, group);
        if (!member) logger.LogInformation("{Sid} is not a member of '{Group}'", sid, group);
        cache.Set(key, member, MembershipTtl);
        return member;
    }

    private bool TokenContains(ClaimsPrincipal principal, string group, NexoraOptions o)
    {
        var groupSid = !string.IsNullOrWhiteSpace(o.AccessGroupSid) ? o.AccessGroupSid : ResolveGroupSidCached(group);
        if (groupSid is null) return false;

        if (principal.Claims.Any(c => c.Type == ClaimTypes.GroupSid
                                      && string.Equals(c.Value, groupSid, StringComparison.OrdinalIgnoreCase)))
            return true;

        return principal is WindowsPrincipal wp && wp.IsInRole(new SecurityIdentifier(groupSid));
    }

    private string? ResolveGroupSidCached(string group)
    {
        var key = "accessgroup:sid:" + group;
        if (cache.TryGetValue(key, out string? cached)) return cached;
        var sid = directory.ResolveGroupSid(group);
        cache.Set(key, sid, sid is null ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(15));
        return sid;
    }
}
