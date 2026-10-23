using System.Security.Claims;
using System.Threading.RateLimiting;
using Nexora.Api.Auth;

namespace Nexora.Api.Http;

/// <summary>Counts HTTP requests to endpoints explicitly using the jobs policy, including rejected submissions. Passive reads/hubs are not charged.</summary>
public static class ActionRequestLimits
{
    public static int Validate(int permits) => permits is >= 1 and <= 10_000 ? permits
        : throw new ArgumentOutOfRangeException(nameof(permits), "Actions:PermitPerMinute must be between 1 and 10000.");
    public static string Partition(ClaimsPrincipal user) => user.GetSid() ?? "anonymous";
    public static FixedWindowRateLimiterOptions Options(int permits) => new()
    {
        PermitLimit=Validate(permits), Window=TimeSpan.FromMinutes(1), QueueLimit=0, AutoReplenishment=true
    };
}
