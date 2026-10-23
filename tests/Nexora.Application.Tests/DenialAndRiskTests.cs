using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Application.Jobs;
using Nexora.Domain;

namespace Nexora.Application.Tests;

/// <summary>S0a: the denial rate cap is per operator and expires; the default risk hook changes nothing.</summary>
public sealed class DenialAndRiskTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);

    private static DenialRateLimiter Limiter() => new(NullLogger<DenialRateLimiter>.Instance);
    private const ErrorKind V = ErrorKind.Validation, F = ErrorKind.Forbidden;

    [Fact]
    public void The_validation_cap_drops_excess_denials_counts_them_and_announces_once()
    {
        var limiter = Limiter();

        var results = Enumerable.Range(0, DenialRateLimiter.MaxValidationPerMinute + 5).Select(_ => limiter.TryAcquire("S-1-5-21-1", V, T0)).ToList();

        results.Count(x => x.Write).Should().Be(DenialRateLimiter.MaxValidationPerMinute);
        limiter.Dropped.Should().Be(5);
        results.Where(x => x.Notices.Count > 0).Should().ContainSingle("one suppression row per operator per window").Which.Notices[0].Kinds.Should().Contain("Validation");
    }

    [Fact]
    public void Forbidden_denials_are_never_starved_by_validation_noise()
    {
        var limiter = Limiter();
        for (var i = 0; i < DenialRateLimiter.MaxValidationPerMinute * 3; i++) limiter.TryAcquire("S-1-5-21-1", V, T0);

        limiter.TryAcquire("S-1-5-21-1", F, T0).Write.Should().BeTrue();
    }

    [Fact]
    public void The_forbidden_cap_is_much_higher_but_still_bounded()
    {
        var limiter = Limiter();
        var writes = Enumerable.Range(0, DenialRateLimiter.MaxForbiddenPerMinute + 10).Count(_ => limiter.TryAcquire("S-1-5-21-1", F, T0).Write);
        writes.Should().Be(DenialRateLimiter.MaxForbiddenPerMinute);
        DenialRateLimiter.MaxForbiddenPerMinute.Should().BeGreaterThan(DenialRateLimiter.MaxValidationPerMinute * 5);
    }

    [Fact]
    public void The_cap_is_per_operator_and_recovers_after_a_minute_with_the_final_count()
    {
        var limiter = Limiter();
        for (var i = 0; i < DenialRateLimiter.MaxValidationPerMinute + 3; i++) limiter.TryAcquire("S-1-5-21-1", V, T0);

        limiter.TryAcquire("S-1-5-21-2", V, T0).Write.Should().BeTrue("another operator has its own budget");
        limiter.TryAcquire("s-1-5-21-1", V, T0.AddSeconds(30)).Write.Should().BeFalse("SIDs compare case-insensitively and the window is still open");
        var after = limiter.TryAcquire("S-1-5-21-1", V, T0.AddSeconds(61));
        after.Write.Should().BeTrue("the window has passed");
        after.Notices.Should().ContainSingle();
        after.Notices[0].WindowClosed.Should().BeTrue();
        after.Notices[0].Dropped.Should().Be(4);
    }

    [Theory]
    [InlineData(RiskLevel.Low)]
    [InlineData(RiskLevel.Medium)]
    [InlineData(RiskLevel.High)]
    [InlineData(RiskLevel.Critical)]
    public async Task The_default_escalator_returns_the_catalog_risk_for_every_action(RiskLevel risk)
    {
        var escalator = new CatalogRiskEscalator();
        foreach (var def in ActionCatalog.All.Where(a => a.Risk == risk))
            (await escalator.EffectiveRiskAsync(def, [], new Dictionary<string, string>(), CancellationToken.None)).Should().Be(risk, def.Code);
    }

    [Fact]
    public void The_confirmation_tiers_by_risk_match_the_catalog_flags()
    {
        foreach (var def in ActionCatalog.All)
        {
            ActionDefinition.NeedsConfirmationAt(def.Risk).Should().Be(def.NeedsConfirmation, def.Code);
            ActionDefinition.NeedsTypedConfirmationAt(def.Risk).Should().Be(def.NeedsTypedConfirmation, def.Code);
        }
    }
}
