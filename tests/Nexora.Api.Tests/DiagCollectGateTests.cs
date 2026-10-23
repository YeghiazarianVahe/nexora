using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application.Jobs;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>diag.collect is catalogued but unimplemented: hidden from every role, refused for Operators, forbidden for Viewers; the exchange host cap is server-side.</summary>
public sealed class DiagCollectGateTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private static Task<HttpResponseMessage> Confirm(HttpClient c, Guid[] ids, object p) =>
        c.PostAsJsonAsync("/api/jobs/confirm", new { actionCode = "diag.collect", endpointIds = ids, parameters = p });

    [Theory]
    [InlineData(Role.Viewer)]
    [InlineData(Role.Operator)]
    [InlineData(Role.Admin)]
    public async Task It_is_never_offered_as_runnable_to_any_role(Role role)
    {
        var client = await factory.UserClientAsync(TestIdentities.NewSid(), role);
        var catalog = (await client.GetFromJsonAsync<List<CatalogEntry>>("/api/jobs/catalog"))!;
        // The test host lists unimplemented actions (demo mode); they are flagged and never runnable.
        catalog.Where(c => c.Code == "diag.collect").Should().OnlyContain(c => !c.Implemented);
    }

    [Fact]
    public async Task A_viewer_cannot_confirm_or_run_it()
    {
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var ep = await factory.SeedEndpointAsync(online: true);

        (await Confirm(viewer, [ep], new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsJsonAsync("/api/jobs", new { actionCode = "diag.collect", endpointIds = new[] { ep }, parameters = new { } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_operator_is_refused_while_it_is_unimplemented_and_nothing_is_created()
    {
        var sid = TestIdentities.NewSid();
        var op = await factory.UserClientAsync(sid, Role.Operator);
        var ep = await factory.SeedEndpointAsync(online: true);

        (await Confirm(op, [ep], new { })).StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        (await op.PostAsJsonAsync("/api/jobs", new { actionCode = "diag.collect", endpointIds = new[] { ep }, parameters = new { } })).StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        factory.Query(db => db.Jobs.Any(j => j.InitiatorSid == sid)).Should().BeFalse();
    }

    [Fact]
    public async Task It_runs_on_one_workstation_only_and_caps_exchange_hosts_at_five()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var a = await factory.SeedEndpointAsync(online: true);
        var b = await factory.SeedEndpointAsync(online: true);

        (await Confirm(op, [a, b], new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Confirm(op, [a], new { exchangeHosts = new[] { "e1", "e2", "e3", "e4", "e5", "e6" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Confirm(op, [a], new { exchangeHosts = new[] { "e1", "e2", "e3", "e4", "e5" } })).StatusCode.Should().Be(HttpStatusCode.NotImplemented, "five hosts pass validation");
        (await Confirm(op, [a], new { collectorIds = new[] { "not-compiled-in" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
