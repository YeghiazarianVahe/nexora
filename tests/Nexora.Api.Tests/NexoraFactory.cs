using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Contracts;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public static class TestIdentities
{
    private static int _next = 10_000;

    public const string SuperAdminSid = "S-1-5-21-1-2-3-1000";
    public const string AccessGroupSid = "S-1-5-21-1-2-3-5000";

    /// <summary>A fresh SID per test, so tests sharing the fixture never see each other's data or cached roles.</summary>
    public static string NewSid() => $"S-1-5-21-1-2-3-{Interlocked.Increment(ref _next)}";
}

/// <summary>Fake Windows auth: identity and group membership come from X-Test-* request headers.</summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string SidHeader = "X-Test-Sid";
    public const string AccountHeader = "X-Test-Account";
    public const string GroupsHeader = "X-Test-Groups";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(SidHeader, out var sid))
            return Task.FromResult(AuthenticateResult.NoResult());

        var account = Request.Headers.TryGetValue(AccountHeader, out var a) ? a.ToString() : "TEST\\user";
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, sid.ToString()), new(ClaimTypes.Name, account) };
        if (Request.Headers.TryGetValue(GroupsHeader, out var groups))
            claims.AddRange(groups.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(g => new Claim(ClaimTypes.GroupSid, g)));

        var identity = new ClaimsIdentity(claims, "Kerberos", ClaimTypes.Name, ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public sealed class FakeDirectory : IDirectoryLookup
{
    public string? GetDisplayName(string sid) => sid == TestIdentities.SuperAdminSid ? "Super Admin" : null;
    public string? ResolveGroupSid(string groupName) => TestIdentities.AccessGroupSid;
    public bool IsUserInGroup(string userSid, string groupNameOrSid) => false;

    public DirectoryUserDto? FindUserBySid(string sid) =>
        sid.StartsWith("S-1-5-21-1-2-3-", StringComparison.Ordinal) ? new DirectoryUserDto(sid, "TEST\\" + sid[^5..], "Dir " + sid[^5..]) : null;

    public IReadOnlyList<DirectoryUserDto> SearchUsers(string query, int max) =>
        [new DirectoryUserDto(TestIdentities.NewSid(), "TEST\\found", "Found " + query)];

    public IReadOnlyList<DirectoryComputer> SearchComputers(string? namePrefix, int max) =>
        [new DirectoryComputer("PC-AD-1", "Windows 11", "Branch A/Workstations")];

    public OuLookupResult ComputerOuMap() => OuLookupResult.Ok(new Dictionary<string, string>());
}

public sealed class FakeCollector : IEndpointCollector
{
    public Task<EndpointSample> CollectSampleAsync(string host, CancellationToken ct) =>
        Task.FromResult(new EndpointSample("10.0.0.1", "TEST\\u", "Windows 11", "10.0", DateTime.UtcNow.AddHours(-3), 10, 20, 30, "PC-REPORTED", "Dell Inc.", "Vostro 3030"));

    /// <summary>Host names for which detail and profile reads fail with an exception whose text must never reach the browser.</summary>
    public static readonly HashSet<string> Failing = new(StringComparer.OrdinalIgnoreCase);
    public const string SecretText = "RPC server 10.99.99.99 \\\\internal-share secret-detail";

    public static EndpointDetailDto? DetailOverride;

    public Task<EndpointDetailDto> CollectDetailAsync(string host, CancellationToken ct) =>
        Failing.Contains(host) ? throw new InvalidOperationException(SecretText) : Task.FromResult(DetailOverride ?? new EndpointDetailDto([], [], [], [], [], []));

    public Task<IReadOnlyList<DiskRaw>> CollectDisksAsync(string host, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DiskRaw>>([
            new DiskRaw("SER1", "Test SSD 512GB", "SER1", "NVMe", "NVMe", 512_000_000_000, "Healthy", false, 41, 80, 1200, 3, null, 0, 0, null, null, null, null, []),
        ]);

    /// <summary>A test may replace the PC's profile list; it must put it back (null) when done.</summary>
    public static IReadOnlyList<ProfileInfo>? ProfilesOverride;

    public Task<IReadOnlyList<ProfileInfo>> CollectProfilesAsync(string host, CancellationToken ct) =>
        Failing.Contains(host) ? throw new InvalidOperationException(SecretText) : ProfilesOverride is { } o ? Task.FromResult(o) : Task.FromResult<IReadOnlyList<ProfileInfo>>([
            new ProfileInfo("S-1-5-21-1-2-3-4001", "TEST\\old.user", "C:\\Users\\old.user", DateTime.UtcNow.AddDays(-90), false, false, false, null),
        ]);
}

/// <summary>Stands in for the "who is signed in" WMI read. Tests set <see cref="Users"/> or <see cref="Fail"/> and put them back when done.</summary>
public sealed class FakeProfileCollector : IProfileCollector
{
    public static SignedInUsers? Users;
    public static bool Fail;

    public Task<SignedInUsers> CollectSignedInAsync(string host, CancellationToken ct) =>
        Fail ? throw new InvalidOperationException(FakeCollector.SecretText) : Task.FromResult(Users ?? new SignedInUsers([], []));
}

/// <summary>Stands in for WMI: never contacts a machine. Tests set <see cref="Behavior"/> and read <see cref="Calls"/>.</summary>
public sealed class FakeHardwareCollector : IHardwareInventoryCollector
{
    public static int Calls;
    public static Func<string, CancellationToken, Task<HardwareInventoryDto>> Behavior = (_, _) => Task.FromResult(Sample());

    public static HardwareInventoryDto Sample() => new("Complete", DateTime.UtcNow, false, false, [],
        new OsInventory("Microsoft Windows 11 Pro", "Professional", "24H2", "26100.4061", "x64", new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)),
        new CpuInventory("Intel", "Intel(R) Core(TM) i5-12500", 6, 12, "x64", 3000),
        new MemoryInventory(17179869184, 17000000000, "DDR4", 3200, 4, 2, 2, 68719476736,
            [new MemoryModuleInfo("DIMM A1", "BANK 0", 8589934592, "Kingston", 3200, 3200, "KF3200", "DDR4"), new MemoryModuleInfo("DIMM B1", "BANK 2", 8589934592, "Kingston", 3200, 3200, "KF3200", "DDR4")], true),
        new BoardInventory("ASUSTeK", "PRIME B660M", "SN-SECRET-123"), new BiosInventory("American Megatrends", "1803", new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc)),
        new ComputerInventory("ASUS", "Desktop PC", "PC-TEST", "corp.local", true));

    public Task<HardwareInventoryDto> CollectHardwareAsync(string host, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Behavior(host, ct);
    }
}

/// <summary>Stands in for the WMI service reads: never contacts a machine. Tests set the per-host data and read <see cref="Calls"/>.</summary>
public sealed class FakeServiceCollector : IServiceCollector
{
    public static int Calls;
    /// <summary>Hosts whose reads fail with an exception whose text must never reach the browser.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> FailingHosts = new(StringComparer.OrdinalIgnoreCase);
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<ServiceInfo>> ServicesByHost = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Key: "host|service".</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<DependentService>> DependentsByKey = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ServiceInfo> Default() =>
    [
        new("Spooler", "Print Spooler", "Running", "Auto"), new("BITS", "Background Intelligent Transfer", "Stopped", "Manual"),
        new("RpcSs", "Remote Procedure Call (RPC)", "Running", "Auto"), new("Fax", "Fax", "Stopped", "Disabled"),
        new("wuauserv", "Windows Update", "Start Pending", "Manual"), new("Dhcp", "DHCP Client", "Running", "Auto"),
    ];

    public Task<IReadOnlyList<ServiceInfo>> CollectServicesAsync(string host, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (FailingHosts.ContainsKey(host)) throw new InvalidOperationException(FakeCollector.SecretText);
        return Task.FromResult(ServicesByHost.TryGetValue(host, out var s) ? s : Default());
    }

    public Task<IReadOnlyList<DependentService>> CollectDependentsAsync(string host, string serviceName, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (FailingHosts.ContainsKey(host)) throw new InvalidOperationException(FakeCollector.SecretText);
        return Task.FromResult(DependentsByKey.TryGetValue(host + "|" + serviceName, out var d) ? d : []);
    }
}

public class NexoraFactory : WebApplicationFactory<Program>
{
    static NexoraFactory()
    {
        // Read eagerly by Program while building services, so they must be real environment variables.
        Environment.SetEnvironmentVariable("Nexora__Database__Provider", "InMemory");
        Environment.SetEnvironmentVariable("Nexora__Database__InMemoryName", "nexora-tests-" + Guid.NewGuid());
        Environment.SetEnvironmentVariable("Nexora__Monitoring__Enabled", "false");
        Environment.SetEnvironmentVariable("Nexora__Actions__PermitPerMinute", "20");
        Environment.SetEnvironmentVariable("Nexora__Monitoring__InventoryTimeoutSeconds", "1");
        Environment.SetEnvironmentVariable("Nexora__SuperAdmin__Sid", TestIdentities.SuperAdminSid);
        Environment.SetEnvironmentVariable("Nexora__SuperAdmin__Account", "TEST\\SuperAdmin");
        Environment.SetEnvironmentVariable("Nexora__AccessGroupSid", TestIdentities.AccessGroupSid);
        Environment.SetEnvironmentVariable("Nexora__ShutdownToken", "test-shutdown-token");
        Environment.SetEnvironmentVariable("Features__DemoActions", "true"); // the suite exercises the simulated and planned catalog entries
        Environment.SetEnvironmentVariable("Nexora__Jobs__Executor", "Fake");
        Environment.SetEnvironmentVariable("Nexora__Jobs__TickMs", "40");
        Environment.SetEnvironmentVariable("Nexora__Jobs__TimeoutScale", "0.05"); // 30 s demo timeout becomes 1.5 s
        Environment.SetEnvironmentVariable("Nexora__Jobs__CancelGraceSeconds", "2");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication(o =>
            {
                o.DefaultScheme = TestAuthHandler.SchemeName;
                o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.AddSingleton<IDirectoryLookup, FakeDirectory>();
            services.AddSingleton<IEndpointCollector, FakeCollector>();
            services.AddSingleton<IHardwareInventoryCollector, FakeHardwareCollector>();
            services.AddSingleton<IServiceCollector, FakeServiceCollector>();
            services.AddSingleton<IProfileCollector, FakeProfileCollector>();
        });
    }

    /// <summary>A client signed in as <paramref name="sid"/>. Pass a null SID for an anonymous caller.</summary>
    public HttpClient ClientFor(string? sid, string account = "TEST\\user", bool inAccessGroup = true)
    {
        var client = CreateClient();
        if (sid is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.SidHeader, sid);
            client.DefaultRequestHeaders.Add(TestAuthHandler.AccountHeader, account);
            if (inAccessGroup)
                client.DefaultRequestHeaders.Add(TestAuthHandler.GroupsHeader, TestIdentities.AccessGroupSid);
        }
        return client;
    }

    /// <summary>Fetches an anti-forgery token (and its cookie) so the client can make state-changing calls.</summary>
    public static async Task<HttpClient> WithTokenAsync(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<TokenResponse>("/api/antiforgery");
        client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", response!.Token);
        return client;
    }

    public async Task<HttpClient> UserClientAsync(string sid, Role? role, string account = "TEST\\user", bool inAccessGroup = true)
    {
        if (role is { } r) await SeedUserAsync(sid, r, account);
        return await WithTokenAsync(ClientFor(sid, account, inAccessGroup));
    }

    public Task<HttpClient> SuperAdminClientAsync() =>
        WithTokenAsync(ClientFor(TestIdentities.SuperAdminSid, "TEST\\SuperAdmin"))!;

    public async Task SeedUserAsync(string sid, Role role, string account = "TEST\\user", UserStatus status = UserStatus.Active)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        db.Users.Add(new User
        {
            Sid = sid, SamAccountName = account, DisplayName = account, Role = role, Status = status, CreatedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public async Task<Guid> SeedEndpointAsync(string hostname = "PC-TEST-1", bool online = false)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        var e = new ManagedEndpoint { Id = Guid.NewGuid(), Hostname = hostname + "-" + Guid.NewGuid().ToString("N")[..6], Online = online, OsCaption = "Microsoft Windows 11 Pro", CreatedUtc = DateTime.UtcNow };
        db.Endpoints.Add(e);
        await db.SaveChangesAsync();
        return e.Id;
    }

    public T Query<T>(Func<INexoraDb, T> query)
    {
        using var scope = Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<INexoraDb>());
    }
}

public sealed record TokenResponse(string Token);
