using System.Text.Json;
using FluentAssertions;
using Nexora.Application.Jobs;
using Nexora.Helper.Actions;

namespace Nexora.Application.Tests;

/// <summary>The interlocks that keep F5 dormant and the Admin setting that feeds the Exchange probe.</summary>
public sealed class DiagnosticSafetyTests
{
    [Fact]
    public void Enabled_collectors_are_pinned_to_shipped_bodies_and_unavailable_to_generic_job_requests()
    {
        var catalog = Nexora.Application.Diagnostics.CollectorCatalog.FromAssembly(typeof(Nexora.Application.Diagnostics.CollectorCatalog).Assembly);
        DiagCollectors.Ids.Should().BeEquivalentTo(catalog.Ids);
        foreach (var id in catalog.Ids) DiagCollectors.Approves(id, catalog.Find(id)!.Sha256).Should().BeTrue();
        ActionCatalog.CollectorIds.Should().BeEmpty();
    }

    [Fact]
    public void The_feature_is_off_by_default_and_the_generic_catalog_entry_stays_unimplemented()
    {
        new FeatureOptions().Diagnostics.Should().BeFalse();
        new FeatureOptions().CredentialOps.Should().BeFalse();
        ActionCatalog.Find("diag.collect")!.Implemented.Should().BeFalse("only the Diagnose endpoint creates this job, never the generic job endpoints");
    }

    [Fact]
    public void Exchange_hosts_default_to_none_and_accept_up_to_five_host_names()
    {
        new PlatformSettings().DiagExchangeHosts.Should().BeEmpty();
        new PlatformSettings().Validate().Should().BeNull();
        new PlatformSettings { DiagExchangeHosts = ["ex01.corp.local", "EX-02", "10.1.2.3", "a", "b"] }.Validate().Should().BeNull();
        new PlatformSettings { DiagExchangeHosts = ["a", "b", "c", "d", "e", "f"] }.Validate().Should().Contain("DiagExchangeHosts");
    }

    [Theory]
    [InlineData("ex01;calc")]
    [InlineData("ex01 ex02")]
    [InlineData("\\\\ex01")]
    [InlineData("http://ex01")]
    [InlineData("ex01:443")]
    [InlineData("-ex01")]
    [InlineData("ex01-")]
    [InlineData("ex01\n")]
    [InlineData("")]
    public void A_host_that_is_not_a_plain_host_name_is_refused(string host) =>
        new PlatformSettings { DiagExchangeHosts = [host] }.Validate().Should().NotBeNull();

    [Fact]
    public void Normalizing_trims_drops_blanks_and_removes_duplicates_ignoring_case()
    {
        var s = new PlatformSettings { DiagExchangeHosts = [" ex01 ", "EX01", "", "  ", "ex02"] };
        s.Normalize();
        s.DiagExchangeHosts.Should().Equal("ex01", "ex02");
    }

    [Fact]
    public void A_document_that_does_not_mention_the_hosts_is_not_treated_as_an_empty_list()
    {
        var older = JsonSerializer.Deserialize<PlatformSettings>("""{"PollIntervalSeconds":60}""")!;
        older.DiagExchangeHosts.Should().BeEmpty();
        older.DiagExchangeHostsSpecified.Should().BeFalse("a save then keeps the stored value");
        JsonSerializer.Deserialize<PlatformSettings>("""{"DiagExchangeHosts":["ex01"]}""")!.DiagExchangeHostsSpecified.Should().BeTrue();
        JsonSerializer.Deserialize<PlatformSettings>("""{"DiagExchangeHosts":[]}""")!.DiagExchangeHostsSpecified.Should().BeTrue("an explicit empty list clears it");
    }
}
