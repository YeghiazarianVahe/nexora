using Microsoft.EntityFrameworkCore;
using Nexora.Domain;

namespace Nexora.Application.Services;

public interface IRoleManagementService
{
    Task<IReadOnlyList<AccessRequestDto>> ListRequestsAsync(string? status, CancellationToken ct = default);
    Task<Result<UserDto>> ApproveAsync(Actor actor, Guid requestId, string role, CancellationToken ct = default);
    Task<Result> RejectAsync(Actor actor, Guid requestId, string reason, CancellationToken ct = default);
    Task<IReadOnlyList<UserDto>> ListUsersAsync(string? search, string? role, CancellationToken ct = default);
    Task<Result<UserDto>> ChangeRoleAsync(Actor actor, string sid, string role, CancellationToken ct = default);
    Task<Result> RevokeAsync(Actor actor, string sid, CancellationToken ct = default);
    Task<Result<UserDto>> AddUserAsync(Actor actor, string sid, string role, CancellationToken ct = default);
    IReadOnlyList<DirectoryUserDto> SearchDirectory(string query);
}

public sealed class RoleManagementService(
    INexoraDb db,
    ISuperAdminChecker superAdmin,
    IDirectoryLookup directory,
    IAuditService audit,
    IUserAccessCache accessCache,
    INexoraNotifier notifier,
    ISystemClock clock) : IRoleManagementService
{
    private const string SuperAdminImmutable = "The SuperAdmin cannot be modified, revoked or assigned.";

    public async Task<IReadOnlyList<AccessRequestDto>> ListRequestsAsync(string? status, CancellationToken ct = default)
    {
        IQueryable<AccessRequest> q = db.AccessRequests.AsNoTracking();
        if (Enum.TryParse<AccessRequestStatus>(status, true, out var s))
            q = q.Where(r => r.Status == s);
        var rows = await q.OrderByDescending(r => r.RequestedUtc).Take(500).ToListAsync(ct);
        return rows.Select(r => r.ToDto()).ToList();
    }

    public async Task<Result<UserDto>> ApproveAsync(Actor actor, Guid requestId, string role, CancellationToken ct = default)
    {
        var parsed = ParseRole(role);
        if (!parsed.Ok) return Result<UserDto>.Fail(parsed.Kind, parsed.Message!);

        var request = await db.AccessRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null) return Result<UserDto>.Fail(ErrorKind.NotFound, "Access request not found.");
        if (request.Status != AccessRequestStatus.Pending)
            return Result<UserDto>.Fail(ErrorKind.Conflict, "The request was already decided.");
        if (superAdmin.IsSuperAdmin(request.Sid))
            return Result<UserDto>.Fail(ErrorKind.Conflict, SuperAdminImmutable);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Sid == request.Sid, ct);
        var old = user is { Status: UserStatus.Active } ? user.Role.ToString() : NexoraRoles.None;
        user = Upsert(user, request.Sid, request.SamAccountName, request.DisplayName, parsed.Value);

        request.Status = AccessRequestStatus.Approved;
        request.GrantedRole = parsed.Value;
        request.DecidedBySid = actor.Sid;
        request.DecidedUtc = clock.UtcNow;

        audit.Add(actor, "role.approve", "User", request.Sid, old, parsed.Value.ToString(),
            new { requestId, account = request.SamAccountName });
        await db.SaveChangesAsync(ct);

        accessCache.Invalidate(request.Sid);
        await notifier.AccessChangedAsync(request.Sid);
        return Result<UserDto>.Success(ToDto(user));
    }

    public async Task<Result> RejectAsync(Actor actor, Guid requestId, string reason, CancellationToken ct = default)
    {
        reason = reason?.Trim() ?? "";
        if (reason.Length is 0 or > 500)
            return Result.Fail(ErrorKind.Validation, "A rejection reason (1-500 characters) is required.");

        var request = await db.AccessRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null) return Result.Fail(ErrorKind.NotFound, "Access request not found.");
        if (request.Status != AccessRequestStatus.Pending)
            return Result.Fail(ErrorKind.Conflict, "The request was already decided.");

        request.Status = AccessRequestStatus.Rejected;
        request.RejectReason = reason;
        request.DecidedBySid = actor.Sid;
        request.DecidedUtc = clock.UtcNow;
        audit.Add(actor, "role.reject", "User", request.Sid, NexoraRoles.None, NexoraRoles.None,
            new { requestId, reason, account = request.SamAccountName });
        await db.SaveChangesAsync(ct);

        await notifier.AccessChangedAsync(request.Sid);
        return Result.Success();
    }

    public async Task<IReadOnlyList<UserDto>> ListUsersAsync(string? search, string? role, CancellationToken ct = default)
    {
        IQueryable<User> q = db.Users.AsNoTracking();
        if (Enum.TryParse<Role>(role, true, out var r)) q = q.Where(u => u.Role == r);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim().ToLower();
            q = q.Where(u => u.SamAccountName.ToLower().Contains(t) || u.DisplayName.ToLower().Contains(t));
        }
        var rows = await q.OrderBy(u => u.DisplayName).Take(1000).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<UserDto>> ChangeRoleAsync(Actor actor, string sid, string role, CancellationToken ct = default)
    {
        if (superAdmin.IsSuperAdmin(sid)) return Result<UserDto>.Fail(ErrorKind.Conflict, SuperAdminImmutable);
        var parsed = ParseRole(role);
        if (!parsed.Ok) return Result<UserDto>.Fail(parsed.Kind, parsed.Message!);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Sid == sid, ct);
        if (user is null) return Result<UserDto>.Fail(ErrorKind.NotFound, "User not found.");
        if (user.Status != UserStatus.Active)
            return Result<UserDto>.Fail(ErrorKind.Conflict, "The user's access is revoked; add the user again instead.");

        var old = user.Role.ToString();
        user.Role = parsed.Value;
        audit.Add(actor, "role.change", "User", sid, old, parsed.Value.ToString(), new { account = user.SamAccountName });
        await db.SaveChangesAsync(ct);

        accessCache.Invalidate(sid);
        await notifier.AccessChangedAsync(sid);
        return Result<UserDto>.Success(ToDto(user));
    }

    public async Task<Result> RevokeAsync(Actor actor, string sid, CancellationToken ct = default)
    {
        if (superAdmin.IsSuperAdmin(sid)) return Result.Fail(ErrorKind.Conflict, SuperAdminImmutable);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Sid == sid, ct);
        if (user is null) return Result.Fail(ErrorKind.NotFound, "User not found.");
        if (user.Status == UserStatus.Revoked) return Result.Fail(ErrorKind.Conflict, "Access is already revoked.");

        var old = user.Role.ToString();
        user.Status = UserStatus.Revoked;
        audit.Add(actor, "role.revoke", "User", sid, old, NexoraRoles.None, new { account = user.SamAccountName });
        await db.SaveChangesAsync(ct);

        accessCache.Invalidate(sid);
        await notifier.AccessChangedAsync(sid);
        return Result.Success();
    }

    public async Task<Result<UserDto>> AddUserAsync(Actor actor, string sid, string role, CancellationToken ct = default)
    {
        if (superAdmin.IsSuperAdmin(sid)) return Result<UserDto>.Fail(ErrorKind.Conflict, SuperAdminImmutable);
        var parsed = ParseRole(role);
        if (!parsed.Ok) return Result<UserDto>.Fail(parsed.Kind, parsed.Message!);

        var found = directory.FindUserBySid(sid);
        if (found is null) return Result<UserDto>.Fail(ErrorKind.NotFound, "No such account in the directory.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Sid == found.Sid, ct);
        if (user is { Status: UserStatus.Active })
            return Result<UserDto>.Fail(ErrorKind.Conflict, "The user already has access; change the role instead.");

        var old = user is null ? NexoraRoles.None : NexoraRoles.None;
        user = Upsert(user, found.Sid, found.Account, found.DisplayName, parsed.Value);

        // A pending self-service request from the same person is satisfied by this grant.
        var pending = await db.AccessRequests
            .Where(r => r.Sid == found.Sid && r.Status == AccessRequestStatus.Pending).ToListAsync(ct);
        foreach (var p in pending)
        {
            p.Status = AccessRequestStatus.Approved;
            p.GrantedRole = parsed.Value;
            p.DecidedBySid = actor.Sid;
            p.DecidedUtc = clock.UtcNow;
        }

        audit.Add(actor, "role.add", "User", found.Sid, old, parsed.Value.ToString(), new { account = found.Account });
        await db.SaveChangesAsync(ct);

        accessCache.Invalidate(found.Sid);
        await notifier.AccessChangedAsync(found.Sid);
        return Result<UserDto>.Success(ToDto(user));
    }

    public IReadOnlyList<DirectoryUserDto> SearchDirectory(string query) =>
        string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2 ? [] : directory.SearchUsers(query.Trim(), 25);

    private User Upsert(User? user, string sid, string account, string display, Role role)
    {
        if (user is null)
        {
            user = new User { Sid = sid, CreatedUtc = clock.UtcNow };
            db.Users.Add(user);
        }
        user.SamAccountName = account;
        user.DisplayName = display;
        user.Role = role;
        user.Status = UserStatus.Active;
        return user;
    }

    private UserDto ToDto(User u) =>
        new(u.Sid, u.SamAccountName, u.DisplayName, u.Role.ToString(), u.Status.ToString(),
            u.CreatedUtc, u.LastSeenUtc, u.LastAuthScheme, superAdmin.IsSuperAdmin(u.Sid));

    /// <summary>SuperAdmin is never assignable, regardless of how the role string is spelled.</summary>
    private static Result<Role> ParseRole(string? role)
    {
        if (string.Equals(role?.Trim(), NexoraRoles.SuperAdmin, StringComparison.OrdinalIgnoreCase))
            return Result<Role>.Fail(ErrorKind.Validation, "SuperAdmin cannot be assigned.");
        if (Enum.TryParse<Role>(role, true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(role, out _))
            return Result<Role>.Success(parsed);
        return Result<Role>.Fail(ErrorKind.Validation, "Role must be Viewer, Operator or Admin.");
    }
}
