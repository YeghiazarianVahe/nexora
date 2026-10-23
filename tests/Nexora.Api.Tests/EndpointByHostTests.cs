using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>S0a: GET /api/endpoints/by-host/{host}, Viewer policy, case-insensitive, with a safe 409 for names that differ only by case.</summary>
public sealed class EndpointByHostTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private async Task<HttpClient> ViewerAsync() => await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

    private async Task<string> SeedAsync(string prefix, params string[] extraCaseVariants)
    {
        var host = prefix + "-" + Guid.NewGuid().ToString("N")[..8];
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        db.Endpoints.Add(new ManagedEndpoint { Id = Guid.NewGuid(), Hostname = host, CreatedUtc = DateTime.UtcNow });
        foreach (var v in extraCaseVariants)
            db.Endpoints.Add(new ManagedEndpoint { Id = Guid.NewGuid(), Hostname = v.Replace("{h}", host.ToUpperInvariant()), CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return host;
    }

    [Fact]
    public async Task A_known_host_is_found_and_the_match_ignores_case()
    {
        var host = await SeedAsync("wks-pi-find");
        var viewer = await ViewerAsync();

        var exact = await viewer.GetAsync($"/api/endpoints/by-host/{host}");
        var upper = await viewer.GetAsync($"/api/endpoints/by-host/{host.ToUpperInvariant()}");

        exact.StatusCode.Should().Be(HttpStatusCode.OK);
        upper.StatusCode.Should().Be(HttpStatusCode.OK);
        (await upper.Content.ReadFromJsonAsync<EndpointSummaryDto>())!.Hostname.Should().Be(host);
    }

    [Fact]
    public async Task An_unknown_host_is_404()
    {
        var viewer = await ViewerAsync();
        (await viewer.GetAsync("/api/endpoints/by-host/no-such-pc-xyz")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Two_rows_that_differ_only_by_case_give_a_safe_409()
    {
        var host = await SeedAsync("wks-pi-dup", "{h}");
        var viewer = await ViewerAsync();

        var r = await viewer.GetAsync($"/api/endpoints/by-host/{host}");

        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await r.Content.ReadAsStringAsync();
        body.Should().NotContainEquivalentOf(host, "the message must not echo host names");
    }

    [Theory]
    [InlineData("bad%20host")]
    [InlineData("-leading-dash")]
    [InlineData("host%0A")]          // a trailing newline must not slip past the pattern ($ would accept it, \z does not)
    [InlineData("a_b")]
    public async Task An_invalid_host_is_400(string host)
    {
        var viewer = await ViewerAsync();
        (await viewer.GetAsync($"/api/endpoints/by-host/{host}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_anonymous_caller_is_rejected()
    {
        var host = await SeedAsync("wks-pi-anon");
        var anon = factory.ClientFor(null);
        (await anon.GetAsync($"/api/endpoints/by-host/{host}")).StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_user_without_a_role_is_rejected()
    {
        var host = await SeedAsync("wks-pi-norole");
        var nobody = await factory.UserClientAsync(TestIdentities.NewSid(), null, inAccessGroup: false);
        (await nobody.GetAsync($"/api/endpoints/by-host/{host}")).StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
