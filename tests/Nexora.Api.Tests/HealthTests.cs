using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Nexora.Api.Tests;

public sealed class HealthTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public async Task Health_is_available_without_sign_in_and_preserves_helper_discovery()
    {
        var response = await factory.ClientFor(null).GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        body.Should().Contain("app", "nexora").And.Contain("status", "ok");
    }
}
