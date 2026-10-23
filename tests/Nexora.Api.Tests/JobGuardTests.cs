using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nexora.Application.Jobs;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>N14, L8, L4: what a confirmation is bound to, when it is spent, and a pattern that runs out of time.</summary>
public sealed class JobGuardTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private static object P(string outcome = "success", int steps = 2, int delayMs = 20) => new { steps, delayMs, outcome };

    private static async Task<string> ConfirmAsync(HttpClient c, string action, Guid[] endpoints, object? parameters, string? reason, string? typed)
    {
        var r = await c.PostAsJsonAsync("/api/jobs/confirm", new { actionCode = action, endpointIds = endpoints, parameters, reason, typed });
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<ConfirmResponse>())!.Token;
    }

    [Fact]
    public async Task A_confirmation_is_bound_to_the_reason_that_was_confirmed()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var host = factory.Query(db => db.Endpoints.Single(e => e.Id == ep).Hostname);
        var token = await ConfirmAsync(admin, "demo.simulate.high", [ep], P(), "Replacing a faulty disk", host);

        var swapped = await admin.PostAsJsonAsync("/api/jobs", new { actionCode = "demo.simulate.high", endpointIds = new[] { ep }, parameters = P(), reason = "something else entirely", confirmToken = token });

        swapped.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the reason was changed after it was confirmed");

        var token2 = await ConfirmAsync(admin, "demo.simulate.high", [ep], P(), "Replacing a faulty disk", host);
        (await admin.PostAsJsonAsync("/api/jobs", new { actionCode = "demo.simulate.high", endpointIds = new[] { ep }, parameters = P(), reason = "Replacing a faulty disk", confirmToken = token2 })).StatusCode
            .Should().Be(HttpStatusCode.Accepted, "the same reason goes through");
    }

    [Fact]
    public async Task A_refused_heavy_job_does_not_burn_the_confirmation()
    {
        var ep = await factory.SeedEndpointAsync(online: true);
        var admin = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Admin);
        var host = factory.Query(db => db.Endpoints.Single(e => e.Id == ep).Hostname);

        var first = await admin.PostAsJsonAsync("/api/jobs", new
        {
            actionCode = "demo.simulate.critical", endpointIds = new[] { ep }, parameters = P("hang", 1, 10), reason = "r",
            confirmToken = await ConfirmAsync(admin, "demo.simulate.critical", [ep], P("hang", 1, 10), "r", host),
        });
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var firstId = (await first.Content.ReadFromJsonAsync<JobDetail>())!.Job.Id;

        var token = await ConfirmAsync(admin, "demo.simulate.critical", [ep], P(), "r", host);
        var body = new { actionCode = "demo.simulate.critical", endpointIds = new[] { ep }, parameters = P(), reason = "r", confirmToken = token };
        (await admin.PostAsJsonAsync("/api/jobs", body)).StatusCode.Should().Be(HttpStatusCode.Conflict, "a heavy job is already running there");

        await admin.PostAsync($"/api/jobs/{firstId}/cancel", null);
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            var d = await admin.GetFromJsonAsync<JobDetail>($"/api/jobs/{firstId}");
            if (d!.Job.Status is "Succeeded" or "Warning" or "PartiallyFailed" or "Failed" or "Cancelled") break;
            await Task.Delay(100);
        }

        (await admin.PostAsJsonAsync("/api/jobs", body)).StatusCode.Should().Be(HttpStatusCode.Accepted, "the same confirmation still works once the conflict is gone");
    }

    [Fact]
    public void A_pattern_that_runs_out_of_time_makes_the_value_invalid_instead_of_throwing()
    {
        var spec = new ParameterSpec("v", ParamType.String, "Value", Required: true, Pattern: @"^(a+)+$", MaxLength: 100);
        var def = new ActionDefinition("t.x", "T", ActionCategories.Services, RiskLevel.Low, Role.Operator, ExecutionMethod.Wmi, [spec], TimeSpan.FromSeconds(10));
        var evil = new string('a', 60) + "!";

        var (values, error) = ParameterValidator.Validate(def, new() { ["v"] = JsonSerializer.SerializeToElement(evil) });

        values.Should().BeNull();
        error.Should().Contain("invalid");
    }
}
