using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Jobs;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>Hardware inventory API: authorization, caching, rate limiting, offline, partial data, timeouts. A fake collector replaces WMI; no machine is contacted.</summary>
public sealed class HardwareInventoryApiTests : IClassFixture<NexoraFactory>, IDisposable
{
    private readonly NexoraFactory _factory;

    public HardwareInventoryApiTests(NexoraFactory factory)
    {
        _factory = factory;
        FakeHardwareCollector.Calls = 0;
        FakeHardwareCollector.Behavior = (_, _) => Task.FromResult(FakeHardwareCollector.Sample());
    }

    public void Dispose() => FakeHardwareCollector.Behavior = (_, _) => Task.FromResult(FakeHardwareCollector.Sample());

    private static Task<HardwareInventoryDto?> Read(HttpResponseMessage r) => r.Content.ReadFromJsonAsync<HardwareInventoryDto>();

    [Fact]
    public async Task Serial_numbers_are_hidden_from_viewers_and_shown_to_operators()
    {
        var id = await _factory.SeedEndpointAsync(online: true);
        var viewer = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var op = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);

        var v = (await Read(await viewer.GetAsync($"/api/endpoints/{id}/hardware")))!;
        v.SerialsHidden.Should().BeTrue();
        v.Board!.Serial.Should().BeNull();
        v.Board.Model.Should().Be("PRIME B660M");
        v.Memory!.Modules.Should().HaveCount(2);

        var o = (await Read(await op.GetAsync($"/api/endpoints/{id}/hardware")))!;
        o.SerialsHidden.Should().BeFalse();
        o.Board!.Serial.Should().Be("SN-SECRET-123");
    }

    [Fact]
    public async Task Refresh_needs_Operator_and_unknown_workstations_are_404()
    {
        var id = await _factory.SeedEndpointAsync(online: true);
        var viewer = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        (await viewer.PostAsync($"/api/endpoints/{id}/hardware/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.GetAsync($"/api/endpoints/{Guid.NewGuid()}/hardware")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _factory.ClientFor(null).GetAsync($"/api/endpoints/{id}/hardware")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_inventory_is_cached_and_a_quick_second_refresh_does_not_query_again()
    {
        var id = await _factory.SeedEndpointAsync(online: true);
        var op = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var before = FakeHardwareCollector.Calls;
        var first = (await Read(await op.GetAsync($"/api/endpoints/{id}/hardware")))!;
        first.Cached.Should().BeFalse();   // the first read collected
        (await Read(await op.GetAsync($"/api/endpoints/{id}/hardware")))!.Cached.Should().BeTrue();
        (await op.PostAsync($"/api/endpoints/{id}/hardware/refresh", null)).EnsureSuccessStatusCode();
        FakeHardwareCollector.Calls.Should().Be(before + 1);
    }

    [Fact]
    public async Task Concurrent_requests_share_one_collection()
    {
        var id = await _factory.SeedEndpointAsync(online: true);
        var op = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        FakeHardwareCollector.Behavior = async (_, ct) => { await Task.Delay(300, ct); return FakeHardwareCollector.Sample(); };
        var before = FakeHardwareCollector.Calls;
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => op.GetAsync($"/api/endpoints/{id}/hardware")));
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        FakeHardwareCollector.Calls.Should().Be(before + 1);
    }

    [Fact]
    public async Task An_offline_workstation_is_not_queried_and_reports_unavailable_instead_of_zeroes()
    {
        var id = await _factory.SeedEndpointAsync(online: false);
        var viewer = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var before = FakeHardwareCollector.Calls;
        var r = (await Read(await viewer.GetAsync($"/api/endpoints/{id}/hardware")))!;
        r.Status.Should().Be("Unavailable");
        r.CollectedUtc.Should().BeNull();
        r.Memory.Should().BeNull();
        r.Cpu.Should().BeNull();
        r.Errors.Should().NotBeEmpty();
        FakeHardwareCollector.Calls.Should().Be(before);
    }

    [Fact]
    public async Task Partial_results_keep_their_errors_and_unknown_values_stay_null()
    {
        var id = await _factory.SeedEndpointAsync(online: true);
        var viewer = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        FakeHardwareCollector.Behavior = (_, _) => Task.FromResult(FakeHardwareCollector.Sample() with
        {
            Status = "Partial", Errors = ["Memory modules: not supported"],
            Memory = new MemoryInventory(null, null, null, null, null, 0, null, null, [], false), Board = null,
        });
        var r = (await Read(await viewer.GetAsync($"/api/endpoints/{id}/hardware")))!;
        r.Status.Should().Be("Partial");
        r.Errors.Should().Contain("Memory modules: not supported");
        r.Memory!.SlotCount.Should().BeNull();
        r.Memory.EmptySlots.Should().BeNull();
        r.Memory.ModulesReported.Should().BeFalse();
        r.Board.Should().BeNull();
    }

    [Fact]
    public async Task A_collection_that_hangs_times_out_and_a_failure_does_not_leak_internals()
    {
        var hang = await _factory.SeedEndpointAsync(online: true);
        var viewer = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        FakeHardwareCollector.Behavior = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return FakeHardwareCollector.Sample(); };
        var started = DateTime.UtcNow;
        var timedOut = (await Read(await viewer.GetAsync($"/api/endpoints/{hang}/hardware")))!;
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(20));
        timedOut.Status.Should().Be("Unavailable");
        timedOut.Errors.Should().ContainMatch("*timed out*");

        var broken = await _factory.SeedEndpointAsync(online: true);
        FakeHardwareCollector.Behavior = (_, _) => throw new InvalidOperationException(FakeCollector.SecretText);
        var failed = await viewer.GetAsync($"/api/endpoints/{broken}/hardware");
        var body = await failed.Content.ReadAsStringAsync();
        failed.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotContain("internal-share").And.NotContain("10.99.99.99");
        (await Read(new HttpResponseMessage { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }))!.Status.Should().Be("Unavailable");
    }

    [Fact]
    public async Task Jobs_can_be_listed_for_one_workstation_and_visibility_rules_still_apply()
    {
        var a = await _factory.SeedEndpointAsync(online: true);
        var b = await _factory.SeedEndpointAsync(online: true);
        var op = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var other = await _factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var created = await op.PostAsJsonAsync("/api/jobs", new { actionCode = "demo.simulate", endpointIds = new[] { a }, parameters = new { steps = 1, delayMs = 10, outcome = "success" } });
        created.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = (await created.Content.ReadFromJsonAsync<JobDetail>())!.Job.Id;

        var forA = await op.GetFromJsonAsync<PagedResult<JobSummary>>($"/api/jobs?endpointId={a}");
        forA!.Items.Select(j => j.Id).Should().Contain(jobId);
        (await op.GetFromJsonAsync<PagedResult<JobSummary>>($"/api/jobs?endpointId={b}"))!.Items.Should().BeEmpty();
        // another operator never sees this operator's job, even when filtering by the workstation
        (await other.GetFromJsonAsync<PagedResult<JobSummary>>($"/api/jobs?endpointId={a}"))!.Items.Should().BeEmpty();
    }
}
