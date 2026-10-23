using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Api.Hubs;
using Nexora.Api.Services;
using Nexora.Application;
using Nexora.Application.Diagnostics;
using Nexora.Application.Jobs;
using Nexora.Contracts;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>How a diagnosis reaches the helper: the collectors travel inside the SIGNED job, and nothing else can put anything there.</summary>
public sealed class DiagnosisDispatchTests : IDisposable
{
    private const string Sid = "S-1-5-21-1-2-3-4242";

    private sealed class Proxy : IClientProxy
    {
        public List<(string Method, object?[] Args)> Sent { get; } = [];
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) { Sent.Add((method, args)); return Task.CompletedTask; }
    }

    private sealed class FakeClients(IClientProxy proxy) : IHubClients
    {
        public IClientProxy All => proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Client(string connectionId) => proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => proxy;
        public IClientProxy Group(string groupName) => proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => proxy;
        public IClientProxy User(string userId) => proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => proxy;
    }

    private sealed class FakeGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CapturingHub : IHubContext<HelperHub>
    {
        private readonly Proxy _proxy = new();
        public CapturingHub() { Clients = new FakeClients(_proxy); }
        public List<(string Method, object?[] Args)> Sent => _proxy.Sent;
        public IHubClients Clients { get; }
        public IGroupManager Groups { get; } = new FakeGroups();
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexora-diag-" + Guid.NewGuid().ToString("N"));
    private readonly ServerTrust _trust;
    private readonly CapturingHub _hub = new();
    private readonly HelperRegistry _registry = new();
    private readonly CollectorCatalog _collectors = CollectorCatalog.FromAssembly(typeof(DependencyInjection).Assembly);
    private readonly ProfileCatalog _profiles;

    public DiagnosisDispatchTests()
    {
        _trust = new ServerTrust(_dir);
        _profiles = ProfileCatalog.FromAssembly(typeof(DependencyInjection).Assembly, _collectors.Ids);
        _registry.Add(Sid, "conn-1");
    }

    public void Dispose()
    {
        _trust.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* best effort */ }
    }

    private HelperJobExecutor Executor(bool flag = true) =>
        new(_registry, _trust, _hub, new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider().GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Options.Create(new FeatureOptions { Diagnostics = flag }), _profiles, _collectors, NullLogger<HelperJobExecutor>.Instance);

    private static JobStartContext Context(Dictionary<string, string> parameters, string action = "diag.collect")
    {
        var job = new Job { Id = Guid.NewGuid(), ActionCode = action, InitiatorSid = Sid, InitiatorAccount = "T", ParametersJson = "{}" };
        var target = new JobTarget { Id = Guid.NewGuid(), JobId = job.Id, EndpointId = Guid.NewGuid(), EndpointName = "PC-1", Host = "PC-1" };
        return new JobStartContext(job, target, ActionCatalog.Find(action)!, parameters, TimeSpan.FromMinutes(15));
    }

    private JobPayload Dispatched()
    {
        var envelope = (JobEnvelope)_hub.Sent.Single(s => s.Method == "ExecuteJobV2").Args[0]!;
        var (payload, error) = JobTrust.Verify(envelope, _trust.PublicKey, Sid, DateTime.UtcNow, _ => true);
        error.Should().Be(JobTrust.Error.None, "the job is signed with the server key, like every job");
        return payload!;
    }

    [Fact]
    public async Task The_collectors_with_their_hashes_travel_inside_the_signed_job_in_the_shape_the_helper_expects()
    {
        var profile = _profiles.Find("outlook-freeze")!;
        var outcome = await Executor().StartAsync(Context(new()
        {
            ["profileId"] = profile.Id, ["runId"] = Guid.NewGuid().ToString(), ["collectorIds"] = string.Join(",", profile.Collectors.Select(c => c.Id)), ["exchangeHosts"] = "exch01.corp.local",
        }), default);

        outcome.Started.Should().BeTrue();
        var payload = Dispatched();
        payload.ActionCode.Should().Be("diag.collect");
        payload.Parameters["exchangeHosts"].Should().Be("exch01.corp.local");
        var items = JsonDocument.Parse(payload.Parameters["collectors"]).RootElement.EnumerateArray().ToList();
        items.Select(i => i.GetProperty("id").GetString()).Should().Equal(profile.Collectors.Select(c => c.Id));
        foreach (var (item, spec) in items.Zip(profile.Collectors))
        {
            var body = item.GetProperty("body").GetString()!;
            body.Should().Be(_collectors.Find(spec.Id)!.Body);
            item.GetProperty("sha256").GetString().Should().Be(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))));
            item.GetProperty("timeoutSeconds").GetInt32().Should().Be(spec.TimeoutSeconds);
            body.Length.Should().BeLessThanOrEqualTo(20_000);
        }
    }

    [Fact]
    public async Task A_collectors_parameter_that_somehow_reached_the_job_row_is_replaced_by_the_server_never_forwarded()
    {
        var profile = _profiles.Find("pc-slow")!;
        await Executor().StartAsync(Context(new()
        {
            ["profileId"] = profile.Id, ["collectorIds"] = "x", ["collectors"] = """[{"id":"x","sha256":"00","body":"calc.exe","timeoutSeconds":5}]""",
        }), default);

        var payload = Dispatched();
        payload.Parameters["collectors"].Should().NotContain("calc.exe");
        JsonDocument.Parse(payload.Parameters["collectors"]).RootElement.EnumerateArray().Select(i => i.GetProperty("id").GetString())
            .Should().Equal(profile.Collectors.Select(c => c.Id));
    }

    [Fact]
    public async Task Nothing_is_sent_when_the_feature_is_off_the_profile_is_unknown_or_the_helper_is_not_connected()
    {
        var good = new Dictionary<string, string> { ["profileId"] = "pc-slow" };

        var off = await Executor(flag: false).StartAsync(Context(good), default);
        var unknown = await Executor().StartAsync(Context(new() { ["profileId"] = "nope" }), default);
        var none = await Executor().StartAsync(Context(new()), default);
        _registry.Remove(Sid, "conn-1");
        var offline = await Executor().StartAsync(Context(good), default);

        off.Started.Should().BeFalse();
        off.Error.Should().Contain("not enabled");
        unknown.Started.Should().BeFalse();
        none.Started.Should().BeFalse();
        offline.Should().Match<StartOutcome>(o => !o.Started && o.WaitForHelper);
        _hub.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Other_actions_get_no_collectors_parameter()
    {
        await Executor().StartAsync(Context(new() { ["steps"] = "1" }, "demo.simulate"), default);

        Dispatched().Parameters.Should().NotContainKey("collectors");
    }
}
