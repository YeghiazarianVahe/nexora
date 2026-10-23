using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Nexora.Api.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public sealed class HelperLifecycleApiTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private HelperDiagnostics Diagnostics => factory.Services.GetRequiredService<HelperDiagnostics>();

    private sealed record Status(bool Connected, bool EverConnected, DateTime? LastConnectedUtc, DateTime? LastDisconnectedUtc, Attempt? LastAttempt, string ServerUrl, bool DownloadAvailable);
    private sealed record Attempt(DateTime AtUtc, string Outcome, string Reason, string? RemoteIp);

    private HubConnection Hub(string path, string sid, bool withToken = false)
    {
        return new HubConnectionBuilder().WithUrl(new Uri(factory.Server.BaseAddress, path), o =>
        {
            o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
            o.Headers[TestAuthHandler.SidHeader] = sid;
            o.Headers[TestAuthHandler.AccountHeader] = "TEST\\user";
            o.Headers[TestAuthHandler.GroupsHeader] = TestIdentities.AccessGroupSid;
            if (withToken)
            {
                using var raw = factory.CreateClient();
                raw.DefaultRequestHeaders.Add(TestAuthHandler.SidHeader, sid);
                raw.DefaultRequestHeaders.Add(TestAuthHandler.AccountHeader, "TEST\\user");
                raw.DefaultRequestHeaders.Add(TestAuthHandler.GroupsHeader, TestIdentities.AccessGroupSid);
                using var response = raw.GetAsync("/api/antiforgery").GetAwaiter().GetResult();
                o.Headers["X-XSRF-TOKEN"] = response.Content.ReadFromJsonAsync<TokenResponse>().GetAwaiter().GetResult()!.Token;
                var cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
                o.Headers["Cookie"] = cookie;
            }
        }).Build();
    }

    // ---------- every attempt is recorded with who, from where, and why it was accepted or refused

    [Fact]
    public async Task A_viewer_is_refused_with_a_reason_and_the_attempt_is_recorded()
    {
        var sid = TestIdentities.NewSid();
        var viewer = await factory.UserClientAsync(sid, Role.Viewer);

        var response = await viewer.PostAsync("/hubs/helper/negotiate?negotiateVersion=1", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var last = Diagnostics.Snapshot(sid).LastAttempt!;
        (last.Outcome, last.Reason).Should().Be(("rejected", "the Viewer role is below Operator"));
        last.Path.Should().Contain("/hubs/helper");
    }

    [Fact]
    public async Task A_user_without_any_role_is_refused_and_told_why()
    {
        var sid = TestIdentities.NewSid();
        var nobody = await factory.UserClientAsync(sid, role: null);

        (await nobody.PostAsync("/hubs/helper/negotiate?negotiateVersion=1", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        Diagnostics.Snapshot(sid).LastAttempt!.Reason.Should().Contain("no Nexora role");
    }

    [Fact]
    public async Task An_operator_is_accepted_and_recorded()
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);

        (await op.PostAsync("/hubs/helper/negotiate?negotiateVersion=1", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var last = Diagnostics.Snapshot(sid).LastAttempt!;
        (last.Outcome, last.Reason).Should().Be(("accepted", "ok"));
    }

    [Theory]
    [InlineData(200, true, "Operator", false, "accepted")]
    [InlineData(101, true, "Admin", false, "accepted")]
    [InlineData(401, false, null, false, "rejected")]
    [InlineData(403, true, null, false, "rejected")]
    [InlineData(403, true, "Viewer", false, "rejected")]
    [InlineData(403, true, "Operator", true, "rejected")]
    [InlineData(404, true, "Operator", false, "rejected")]
    public void The_reason_text_covers_every_outcome(int status, bool authenticated, string? role, bool crossOrigin, string outcome)
    {
        var (o, reason) = HelperDiagnostics.Decide(status, authenticated, role, crossOrigin);

        o.Should().Be(outcome);
        reason.Should().NotBeNullOrWhiteSpace();
        if (crossOrigin) reason.Should().Contain("cross-origin");
        if (status == 401) reason.Should().Contain("Windows sign-in");
    }

    // ---------- status, and the push that clears the banner without a refresh

    [Fact]
    public async Task Status_says_never_connected_then_connected_then_dropped_and_each_change_is_pushed_to_the_browser()
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);

        var first = (await op.GetFromJsonAsync<Status>("/api/helper/status"))!;
        (first.Connected, first.EverConnected, first.LastAttempt).Should().Be((false, false, null));
        first.ServerUrl.Should().StartWith("http://");

        var pushes = new List<bool>();
        await using var browser = Hub("/hubs/nexora", sid, withToken: true);
        browser.On<System.Text.Json.JsonElement>("HelperStatusChanged", e => { lock (pushes) pushes.Add(e.GetProperty("connected").GetBoolean()); });
        await browser.StartAsync();
        // Handshake completion can precede OnConnectedAsync group enrollment.
        // A round-trip invocation is a deterministic enrollment barrier before a separate helper emits its push.
        await browser.InvokeAsync("SubscribeEndpoint",Guid.Empty);
        await browser.InvokeAsync("UnsubscribeEndpoint",Guid.Empty);

        await using var helper = Hub("/hubs/helper", sid);
        await helper.StartAsync();
        await WaitAsync(() => { lock (pushes) return pushes.Contains(true); });

        var connected = (await op.GetFromJsonAsync<Status>("/api/helper/status"))!;
        (connected.Connected, connected.EverConnected).Should().Be((true, true));
        connected.LastAttempt!.Outcome.Should().Be("accepted");

        await helper.StopAsync();
        await WaitAsync(() => { lock (pushes) return pushes.Contains(false); });

        var dropped = (await op.GetFromJsonAsync<Status>("/api/helper/status"))!;
        (dropped.Connected, dropped.EverConnected).Should().Be((false, true), "it was connected and is gone");
        dropped.LastDisconnectedUtc.Should().NotBeNull();

        // And it comes back: the banner is told again, with no page refresh.
        await using var again = Hub("/hubs/helper", sid);
        await again.StartAsync();
        await WaitAsync(() => { lock (pushes) return pushes.Count(p => p) >= 2; });
        (await op.GetFromJsonAsync<Status>("/api/helper/status"))!.Connected.Should().BeTrue();

        await again.StopAsync();
        await browser.StopAsync();
    }

    private static async Task WaitAsync(Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) { if (condition()) return; await Task.Delay(40); }
        throw new TimeoutException("The expected push did not arrive.");
    }

    // ---------- download, and the one address

    [Fact]
    public async Task The_helper_download_needs_the_Operator_role_and_says_so_when_it_is_not_published()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        (await viewer.GetAsync("/api/helper/download")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var missing = await op.GetAsync("/api/helper/download");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missing.Content.ReadAsStringAsync()).Should().Contain("not published");
        (await factory.ClientFor(null).GetAsync("/api/helper/download")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_identifies_the_server_as_Nexora_so_a_helper_can_tell_it_from_another_application()
    {
        var json = (await factory.ClientFor(null).GetFromJsonAsync<System.Text.Json.JsonElement>("/health"))!;
        (json.GetProperty("status").GetString(), json.GetProperty("app").GetString()).Should().Be(("ok", "nexora"));
    }

    [Fact]
    public void The_server_defaults_to_port_5090_like_the_helper_and_the_scripts()
    {
        new Nexora.Application.NexoraOptions().Url.Should().Be("http://+:5090/");
        var root = FindRepoRoot();
        File.ReadAllText(Path.Combine(root, "src", "Nexora.Api", "appsettings.json")).Should().Contain("5090").And.NotContain("5080");
        File.ReadAllText(Path.Combine(root, "nexora.config.ps1")).Should().Contain("$NexoraPort = 5090");
        File.ReadAllText(Path.Combine(root, "scripts", "nexora.ps1")).Should().Contain("[int]$Port = 5090");
        Nexora.Contracts.HelperDefaults.Server.Should().Be("http://wks-111:5090");
    }

    [Fact]
    public void No_source_file_still_points_at_port_5080_for_Nexora()
    {
        var root = FindRepoRoot();
        var offenders = new[] { "src", "scripts" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.*", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}wwwroot{Path.DirectorySeparatorChar}")
                        && new[] { ".cs", ".ts", ".tsx", ".json", ".ps1" }.Contains(Path.GetExtension(f)))
            .Where(f => File.ReadAllText(f).Contains("5080"))
            .Select(f => Path.GetRelativePath(root, f)).ToList();

        offenders.Should().BeEmpty("5080 is the Service Desk Console's port");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexora.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
