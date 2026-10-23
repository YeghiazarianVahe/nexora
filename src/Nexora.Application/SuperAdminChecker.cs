using Microsoft.Extensions.Options;

namespace Nexora.Application;

public interface ISuperAdminChecker
{
    bool IsSuperAdmin(string? sid);

    /// <summary>True for the SuperAdmin SID when the account name differs from the configured label (case-insensitive).</summary>
    bool AccountLabelMismatch(string? sid, string? account);
}

public sealed class SuperAdminChecker(IOptions<NexoraOptions> options) : ISuperAdminChecker
{
    private readonly SuperAdminOptions _config = options.Value.SuperAdmin;

    public bool IsSuperAdmin(string? sid) =>
        !string.IsNullOrWhiteSpace(sid)
        && !string.IsNullOrWhiteSpace(_config.Sid)
        && string.Equals(sid.Trim(), _config.Sid.Trim(), StringComparison.OrdinalIgnoreCase);

    public bool AccountLabelMismatch(string? sid, string? account) =>
        IsSuperAdmin(sid)
        && !string.IsNullOrWhiteSpace(_config.Account)
        && !string.Equals(account?.Trim(), _config.Account.Trim(), StringComparison.OrdinalIgnoreCase);
}
