using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace Nexora.Helper;

/// <summary>Opens CIM (DCOM) sessions with the operator's own token: this process runs in the operator's session and uses default credentials.</summary>
public static class JobExecutor
{
    internal static readonly TimeSpan CimTimeout = TimeSpan.FromSeconds(30);

    internal static CimSession Open(string host)
    {
        if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return CimSession.Create(null);
        return CimSession.Create(host, new DComSessionOptions { Timeout = CimTimeout });
    }
}
