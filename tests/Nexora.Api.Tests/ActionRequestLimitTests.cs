using System.Security.Claims;
using System.Threading.RateLimiting;
using FluentAssertions;
using Nexora.Api.Http;
using System.Net;
using System.Net.Http.Json;
using Nexora.Domain;

namespace Nexora.Api.Tests;
public sealed class ActionRequestLimitTests
{
    [Fact]
    public async Task Http_policy_throttles_submissions_per_sid_without_throttling_passive_reads()
    {
        using var factory=new NexoraFactory();
        using var first=await factory.UserClientAsync(TestIdentities.NewSid(),Role.Operator);
        using var second=await factory.UserClientAsync(TestIdentities.NewSid(),Role.Operator);
        for(var i=0;i<20;i++)
            (await first.PostAsJsonAsync("/api/jobs",new{actionCode="invalid.audit.fixture",endpointIds=Array.Empty<Guid>()})).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await first.PostAsJsonAsync("/api/jobs",new{actionCode="invalid.audit.fixture",endpointIds=Array.Empty<Guid>()})).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await second.PostAsJsonAsync("/api/jobs",new{actionCode="invalid.audit.fixture",endpointIds=Array.Empty<Guid>()})).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await first.GetAsync("/api/jobs/catalog")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
    [Fact]
    public async Task Https_security_headers_allow_wss_without_enabling_a_binding()
    {
        var context=new Microsoft.AspNetCore.Http.DefaultHttpContext();context.Request.Scheme="https";context.Request.Host=new("approved-host",5091);
        await new SecurityHeadersMiddleware(_=>Task.CompletedTask).InvokeAsync(context);
        context.Response.Headers.ContentSecurityPolicy.ToString().Should().Contain("wss://approved-host:5091").And.NotContain(" ws://");
    }
    [Theory]
    [InlineData(1)] [InlineData(20)] [InlineData(60)] [InlineData(75)]
    public void Configured_limits_are_exact_and_excess_requests_are_rejected(int permits)
    {
        using var limiter=new FixedWindowRateLimiter(ActionRequestLimits.Options(permits));
        using var permitted=limiter.AttemptAcquire(permits);
        permitted.IsAcquired.Should().BeTrue();
        using var excess=limiter.AttemptAcquire();
        excess.IsAcquired.Should().BeFalse();
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(10001)]
    public void Invalid_limits_are_configuration_errors(int permits) =>
        FluentActions.Invoking(()=>ActionRequestLimits.Validate(permits)).Should().Throw<ArgumentOutOfRangeException>();
    [Fact]
    public void Different_sids_have_independent_budgets()
    {
        static ClaimsPrincipal User(string sid)=>new(new ClaimsIdentity(new[]{new Claim(ClaimTypes.PrimarySid,sid)},"Test"));
        using var partitioned=PartitionedRateLimiter.Create<ClaimsPrincipal,string>(user=>RateLimitPartition.GetFixedWindowLimiter(ActionRequestLimits.Partition(user),_=>ActionRequestLimits.Options(1)));
        using var first=partitioned.AttemptAcquire(User("S-1-A"));
        using var excess=partitioned.AttemptAcquire(User("S-1-A"));
        using var second=partitioned.AttemptAcquire(User("S-1-B"));
        first.IsAcquired.Should().BeTrue(); excess.IsAcquired.Should().BeFalse(); second.IsAcquired.Should().BeTrue();
    }
}
