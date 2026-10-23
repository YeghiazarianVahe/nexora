using System.Net.Http.Json;
using FluentAssertions;
using Nexora.Api.Services;
using Nexora.Application;
using Nexora.Contracts;
using Nexora.Domain;

namespace Nexora.Api.Tests;

/// <summary>N1: the server signs every job with a persisted key; helpers pin its public half.</summary>
public sealed class ServerTrustTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    [Fact]
    public void The_key_survives_a_restart_and_is_not_stored_in_the_clear()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexora-trust-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = new ServerTrust(dir);
            using var second = new ServerTrust(dir);
            second.PublicKey.Should().Equal(first.PublicKey);

            var pkcs8 = System.Security.Cryptography.ECDsa.Create();
            File.ReadAllBytes(Path.Combine(dir, "server-trust.key")).Length.Should().BeGreaterThan(0);
            var raw = File.ReadAllBytes(Path.Combine(dir, "server-trust.key"));
            raw.AsSpan().IndexOf(first.PublicKey.AsSpan(0, 16)).Should().Be(-1, "the file is DPAPI-protected");
            pkcs8.Dispose();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_damaged_key_file_makes_a_new_identity_and_old_signatures_stop_verifying()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexora-trust-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = new ServerTrust(dir);
            var oldKey = first.PublicKey;
            var payload = new JobPayload(Guid.NewGuid(), Guid.NewGuid(), "demo.simulate", "PC", "S-1", new(), 30, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2), "n");
            File.WriteAllBytes(Path.Combine(dir, "server-trust.key"), [1, 2, 3]);

            using var rotated = new ServerTrust(dir);

            rotated.PublicKey.Should().NotEqual(oldKey);
            JobTrust.Verify(rotated.Sign(payload), oldKey, "S-1", DateTime.UtcNow, _ => true).Error.Should().Be(JobTrust.Error.BadSignature, "helpers carrying the old key refuse the new server");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task The_fingerprint_endpoint_matches_what_the_server_signs_with()
    {
        var op = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Operator);
        var body = (await op.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/helper/server-key"));
        var trust = factory.Services.GetRequiredService<ServerTrust>();
        body.GetProperty("fingerprint").GetString().Should().Be(trust.Fingerprint);
        Convert.FromBase64String(body.GetProperty("publicKey").GetString()!).Should().Equal(trust.PublicKey);

        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        (await viewer.GetAsync("/api/helper/server-key")).IsSuccessStatusCode.Should().BeFalse();
    }
}
