using Microsoft.EntityFrameworkCore;
using Nexora.Domain;

namespace Nexora.Application;

public sealed record MeDto(
    string Sid, string Account, string DisplayName, string Role, bool IsSuperAdmin, string Status, string? Detail = null);

/// <summary>Values of <see cref="MeDto.Status"/>.</summary>
public static class MeStatus
{
    public const string Active = "Active";

    /// <summary>In the access group, no record yet: the client should submit an access request.</summary>
    public const string NoAccess = "NoAccess";
    public const string Pending = "Pending";
    public const string Rejected = "Rejected";
    public const string Revoked = "Revoked";

    /// <summary>Not a member of the access group: nothing is stored and nothing can be requested.</summary>
    public const string Denied = "Denied";
}

public interface IMeService
{
    Task<MeDto> BuildAsync(string sid, string account, bool isGroupMember, CancellationToken ct = default);
}

public sealed class MeService(ISuperAdminChecker superAdmin, IDirectoryLookup directory, INexoraDb db) : IMeService
{
    public async Task<MeDto> BuildAsync(string sid, string account, bool isGroupMember, CancellationToken ct = default)
    {
        var display = directory.GetDisplayName(sid) ?? account;

        if (superAdmin.IsSuperAdmin(sid))
            return new MeDto(sid, account, display, NexoraRoles.SuperAdmin, true, MeStatus.Active);

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Sid == sid, ct);
        if (user is { Status: UserStatus.Active })
            return new MeDto(sid, account, display, user.Role.ToString(), false, MeStatus.Active);

        if (user is { Status: UserStatus.Revoked })
            return new MeDto(sid, account, display, NexoraRoles.None, false, MeStatus.Revoked, "Your access was revoked.");

        if (!isGroupMember)
            return new MeDto(sid, account, display, NexoraRoles.None, false, MeStatus.Denied,
                "Only members of the IT Service Desk group can use Nexora.");

        var latest = await db.AccessRequests.AsNoTracking()
            .Where(r => r.Sid == sid).OrderByDescending(r => r.RequestedUtc).FirstOrDefaultAsync(ct);
        return latest?.Status switch
        {
            AccessRequestStatus.Pending => new MeDto(sid, account, display, NexoraRoles.None, false, MeStatus.Pending),
            AccessRequestStatus.Rejected => new MeDto(sid, account, display, NexoraRoles.None, false, MeStatus.Rejected, latest.RejectReason),
            _ => new MeDto(sid, account, display, NexoraRoles.None, false, MeStatus.NoAccess),
        };
    }
}
