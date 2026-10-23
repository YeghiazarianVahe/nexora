using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Nexora.Application;
using Nexora.Domain;

namespace Nexora.Api.Auth;

/// <summary>
/// Works out Kerberos vs NTLM. WindowsIdentity only says "Negotiate", so inspect the SPNEGO token on the
/// request that opened the connection and remember the answer per connection.
/// </summary>
public sealed class AuthSchemeDetector(IMemoryCache cache)
{
    private static readonly byte[] NtlmSsp = "NTLMSSP\0"u8.ToArray();
    private static readonly byte[] KerberosOid = [0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x12, 0x01, 0x02, 0x02];

    public string Detect(HttpContext? context, string? identityAuthType)
    {
        var fallback = string.IsNullOrEmpty(identityAuthType) ? "unknown" : identityAuthType;
        if (context is null) return fallback;

        var key = "authscheme:" + context.Connection.Id;
        var header = context.Request.Headers.Authorization.ToString();
        var detected = FromHeader(header);
        if (detected is not null)
        {
            cache.Set(key, detected, TimeSpan.FromMinutes(30));
            return detected;
        }
        return cache.TryGetValue(key, out string? known) && known is not null ? known : fallback;
    }

    public static string? FromHeader(string header)
    {
        if (header.StartsWith("NTLM ", StringComparison.OrdinalIgnoreCase)) return "NTLM";
        if (!header.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var token = Convert.FromBase64String(header["Negotiate ".Length..].Trim());
            if (token.AsSpan().IndexOf(NtlmSsp) >= 0) return "NTLM";
            if (token.AsSpan().IndexOf(KerberosOid) >= 0) return "Kerberos";
        }
        catch (FormatException) { }
        return null;
    }
}

/// <summary>Adds the Nexora role claim: SuperAdmin by configured SID, everyone else from the database (cached 60 s).</summary>
public sealed class NexoraClaimsTransformation(
    ISuperAdminChecker superAdmin,
    IUserAccessCache accessCache,
    AuthSchemeDetector schemeDetector,
    IHttpContextAccessor httpContext,
    ILogger<NexoraClaimsTransformation> logger) : IClaimsTransformation
{
    private const string TransformedMarker = "Nexora";

    // Last auth scheme logged per SID, so we log once per login/scheme change rather than per request.
    private static readonly ConcurrentDictionary<string, string> LoggedSchemes = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var sid = principal.GetSid();
        if (principal.Identity is not { IsAuthenticated: true } || sid is null)
            return principal;
        if (principal.Identities.Any(i => i.AuthenticationType == TransformedMarker))
            return principal;

        var scheme = schemeDetector.Detect(httpContext.HttpContext, principal.Identity.AuthenticationType);
        LogLogin(principal, sid, scheme);

        string? role = null;
        if (superAdmin.IsSuperAdmin(sid))
        {
            role = NexoraRoles.SuperAdmin;
        }
        else
        {
            var access = await accessCache.GetAsync(sid, scheme);
            if (access is { Status: UserStatus.Active, Role: { } r }) role = r.ToString();
        }

        var identity = new ClaimsIdentity(TransformedMarker);
        // A stable per-user id keeps anti-forgery tokens valid across role changes (the role claim would otherwise be part of the token binding).
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, sid));
        if (role is not null) identity.AddClaim(new Claim(ClaimTypes.Role, role));
        principal.AddIdentity(identity);
        return principal;
    }

    private void LogLogin(ClaimsPrincipal principal, string sid, string scheme)
    {
        if (LoggedSchemes.TryGetValue(sid, out var previous) && previous == scheme) return;
        LoggedSchemes[sid] = scheme;

        var account = principal.GetAccount();
        logger.LogInformation("Login {Account} ({Sid}) authenticated via {AuthScheme}", account, sid, scheme);
        if (string.Equals(scheme, "NTLM", StringComparison.OrdinalIgnoreCase))
            logger.LogInformation("{Account} is using NTLM; for Kerberos use the short hostname and trust it for SSO in the browser", account);
        if (superAdmin.AccountLabelMismatch(sid, account))
            logger.LogWarning("SuperAdmin SID {Sid} resolved to account {Account}, which differs from the configured Account label", sid, account);
    }
}
