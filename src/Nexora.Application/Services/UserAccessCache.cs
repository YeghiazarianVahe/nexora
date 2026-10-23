using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Domain;

namespace Nexora.Application.Services;

/// <summary>
/// Role lookup for the claims transformation: cached for 60 s, invalidated on every role change.
/// A cache miss also refreshes LastSeen / LastAuthScheme, so those are written at most once a minute per user.
/// </summary>
public sealed class UserAccessCache(IMemoryCache cache, IServiceScopeFactory scopes, ISystemClock clock) : IUserAccessCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public async Task<CachedAccess> GetAsync(string sid, string? authScheme, CancellationToken ct = default)
    {
        if (cache.TryGetValue(Key(sid), out CachedAccess? hit) && hit is not null)
            return hit;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Sid == sid, ct);
        if (user is not null)
        {
            user.LastSeenUtc = clock.UtcNow;
            if (!string.IsNullOrEmpty(authScheme)) user.LastAuthScheme = authScheme;
            await db.SaveChangesAsync(ct);
        }

        var result = new CachedAccess(user?.Role, user?.Status);
        cache.Set(Key(sid), result, Ttl);
        return result;
    }

    public void Invalidate(string sid) => cache.Remove(Key(sid));

    private static string Key(string sid) => "access:" + sid.ToUpperInvariant();
}
