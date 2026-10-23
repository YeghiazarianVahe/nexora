using Microsoft.EntityFrameworkCore;
using Nexora.Domain;

namespace Nexora.Application.Services;

public interface IAccessRequestService
{
    /// <summary>Creates a Pending request. Non-members of the access group get Forbidden and no row is written.</summary>
    Task<Result<AccessRequestDto>> RequestAsync(Actor actor, bool isGroupMember, CancellationToken ct = default);
}

public sealed class AccessRequestService(
    INexoraDb db,
    ISuperAdminChecker superAdmin,
    IDirectoryLookup directory,
    IAuditService audit,
    INexoraNotifier notifier,
    ISystemClock clock) : IAccessRequestService
{
    public async Task<Result<AccessRequestDto>> RequestAsync(Actor actor, bool isGroupMember, CancellationToken ct = default)
    {
        if (!isGroupMember)
            return Result<AccessRequestDto>.Fail(ErrorKind.Forbidden, "Only members of the IT Service Desk group can request access.");

        if (superAdmin.IsSuperAdmin(actor.Sid))
            return Result<AccessRequestDto>.Fail(ErrorKind.Conflict, "SuperAdmin does not need an access request.");

        var existingUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Sid == actor.Sid, ct);
        if (existingUser is { Status: UserStatus.Active })
            return Result<AccessRequestDto>.Fail(ErrorKind.Conflict, "You already have access.");
        if (existingUser is { Status: UserStatus.Revoked })
            return Result<AccessRequestDto>.Fail(ErrorKind.Forbidden, "Your access was revoked. Contact the SuperAdmin.");

        var pending = await db.AccessRequests.FirstOrDefaultAsync(
            r => r.Sid == actor.Sid && r.Status == AccessRequestStatus.Pending, ct);
        if (pending is not null)
            return Result<AccessRequestDto>.Success(pending.ToDto());

        var request = new AccessRequest
        {
            Id = Guid.NewGuid(),
            Sid = actor.Sid,
            SamAccountName = actor.Account,
            DisplayName = directory.GetDisplayName(actor.Sid) ?? actor.Account,
            Status = AccessRequestStatus.Pending,
            RequestedUtc = clock.UtcNow,
        };
        db.AccessRequests.Add(request);
        audit.Add(actor, "access.request", "AccessRequest", request.Id.ToString());
        await db.SaveChangesAsync(ct);

        var dto = request.ToDto();
        await notifier.AccessRequestPendingAsync(dto);
        return Result<AccessRequestDto>.Success(dto);
    }
}
