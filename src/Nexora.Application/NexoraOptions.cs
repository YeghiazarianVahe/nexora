namespace Nexora.Application;

public sealed class NexoraOptions
{
    public const string SectionName = "Nexora";

    public string Url { get; set; } = "http://+:5090/";

    /// <summary>AD group whose members may request access.</summary>
    public string AccessGroup { get; set; } = "IT Service Desk";

    /// <summary>Optional: the group's SID, used instead of resolving <see cref="AccessGroup"/> from AD.</summary>
    public string? AccessGroupSid { get; set; }

    /// <summary>Shared secret that lets nexora.ps1 request a graceful shutdown from loopback.</summary>
    public string? ShutdownToken { get; set; }

    /// <summary>
    /// The address operators use to reach Nexora (for example <c>http://wks-111:5090</c>). The install command shown to operators is built from
    /// it, never from the request's Host header, which a caller controls. Empty: this PC's name and the port of <see cref="Url"/>.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>The configured public address, or http://MACHINE:port derived from <see cref="Url"/>.</summary>
    public string ResolvePublicUrl()
    {
        if (!string.IsNullOrWhiteSpace(PublicUrl) && Uri.TryCreate(PublicUrl.Trim(), UriKind.Absolute, out var configured)
            && configured.Scheme is "http" or "https") return configured.GetLeftPart(UriPartial.Authority);
        var port = 5090;
        if (Uri.TryCreate(Url.Replace("+", "localhost").Replace("*", "localhost"), UriKind.Absolute, out var listen) && listen.Port > 0) port = listen.Port;
        return $"http://{Environment.MachineName.ToLowerInvariant()}:{port}";
    }

    /// <summary>The operator PCs whose helper coverage the Admin view tracks (host names). Seeded in appsettings.json; add a name to track a new PC.</summary>
    public List<string> OperatorPcs { get; set; } = [];

    public SuperAdminOptions SuperAdmin { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public MonitoringOptions Monitoring { get; set; } = new();
    public ActionsOptions Actions { get; set; } = new();
    public Jobs.JobsOptions Jobs { get; set; } = new();
}

public sealed class SuperAdminOptions
{
    /// <summary>Authoritative identifier, matched by SID.</summary>
    public string Sid { get; set; } = "";

    /// <summary>Readable label and sanity check only; never used for authorization.</summary>
    public string Account { get; set; } = "";
}

public sealed class DatabaseOptions
{
    /// <summary>Npgsql (default) or InMemory (tests).</summary>
    public string Provider { get; set; } = "Npgsql";
    public bool AutoMigrate { get; set; } = true;
    public string InMemoryName { get; set; } = "nexora";
}

public sealed class MonitoringOptions
{
    public bool Enabled { get; set; } = true;
    public int ConnectTimeoutSeconds { get; set; } = 15;
    /// <summary>At most this many PCs get a full drive scan in one cycle; the rest stay due for the next cycles (a rescan-all must not scan the whole fleet at once).</summary>
    public int MaxDiskScansPerCycle { get; set; } = 25;
    /// <summary>How long a collected hardware/OS inventory is served from memory before it is read again on the next view.</summary>
    public int InventoryTtlMinutes { get; set; } = 60;
    /// <summary>Hard limit for one inventory collection (all WMI sections together).</summary>
    public int InventoryTimeoutSeconds { get; set; } = 45;
    /// <summary>A manual refresh of the same workstation inside this window returns the last result instead of querying again.</summary>
    public int InventoryMinRefreshSeconds { get; set; } = 15;
    /// <summary>Hard limit for one on-demand read of a workstation's services or their dependents (also bounds the risk check of a stop or restart).</summary>
    public int ServiceReadTimeoutSeconds { get; set; } = 15;
    /// <summary>Hard limit for one on-demand read of a workstation's user profiles (profile list, last-used lookups and who is signed in).</summary>
    public int ProfileReadTimeoutSeconds { get; set; } = 45;
}

public sealed class ActionsOptions
{
    public int PermitPerMinute { get; set; } = 20;
    public int JobTtlSeconds { get; set; } = 120;
}

public sealed class FeatureOptions
{
    public const string SectionName = "Features";

    /// <summary>Password reset and any credential handling. Must stay false while the app serves plain HTTP.</summary>
    public bool CredentialOps { get; set; }

    /// <summary>Lists the simulated "Simulate..." actions and the planned-but-not-yet-implemented catalog entries. Off in production.</summary>
    public bool DemoActions { get; set; }

    /// <summary>
    /// The deterministic Diagnose engine (F5). OFF by default: with it off the start endpoint answers 501 and the diag.collect job is never dispatched.
    /// Even when ON, a shipped helper refuses every collector because its compiled allow-list is empty (see ADR-0006).
    /// </summary>
    public bool Diagnostics { get; set; }
}
