using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Nexora.Api.Hubs;
using Nexora.Application;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>M1 and N4: a revoked role cuts live connections at once, and a helper's disconnect is tracked without touching the dead identity.</summary>
public sealed class RevocationTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private HubConnection Helper(string sid) =>
        new HubConnectionBuilder().WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/helper"), o =>
        {
            o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
            o.Headers[TestAuthHandler.SidHeader] = sid;
            o.Headers[TestAuthHandler.AccountHeader] = "TEST-user";
            o.Headers[TestAuthHandler.GroupsHeader] = TestIdentities.AccessGroupSid;
        }).Build();

    private static async Task WaitAsync(Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) { if (condition()) return; await Task.Delay(40); }
        throw new TimeoutException("The expected change did not happen.");
    }

    [Fact]
    public async Task A_helper_that_disconnects_is_forgotten_by_the_tracker_and_the_registry()
    {
        var sid = TestIdentities.NewSid();
        await factory.SeedUserAsync(sid, Role.Operator);
        var tracker = factory.Services.GetRequiredService<HubConnectionTracker>();
        var registry = factory.Services.GetRequiredService<HelperRegistry>();

        var helper = Helper(sid);
        await helper.StartAsync();
        await WaitAsync(() => tracker.Count(sid) == 1 && registry.Find(sid) is not null);

        await helper.StopAsync();

        await WaitAsync(() => tracker.Count(sid) == 0 && registry.Find(sid) is null);
    }

    [Fact]
    public async Task Revoking_a_role_closes_the_live_helper_connection_and_the_next_attempt_is_refused()
    {
        var sid = TestIdentities.NewSid();
        await factory.SeedUserAsync(sid, Role.Operator);
        var closed = new TaskCompletionSource();
        var helper = Helper(sid);
        helper.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await helper.StartAsync();
        var tracker = factory.Services.GetRequiredService<HubConnectionTracker>();
        await WaitAsync(() => tracker.Count(sid) == 1);

        // revoke: the role is removed, then the notifier is told, as RoleManagementService does
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
            db.Users.Single(u => u.Sid == sid).Status = UserStatus.Revoked;
            await db.SaveChangesAsync();
            scope.ServiceProvider.GetRequiredService<IUserAccessCache>().Invalidate(sid);
            await scope.ServiceProvider.GetRequiredService<INexoraNotifier>().AccessChangedAsync(sid);
        }

        (await Task.WhenAny(closed.Task, Task.Delay(8000))).Should().Be(closed.Task, "the live connection is dropped immediately, not at its next reconnect");
        tracker.Count(sid).Should().Be(0);

        var again = Helper(sid);
        var attempt = async () => await again.StartAsync();
        await attempt.Should().ThrowAsync<Exception>("a revoked operator's helper is refused");
    }
}
