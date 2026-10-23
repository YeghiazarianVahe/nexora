using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Jobs;

namespace Nexora.Application.Tests;

public sealed class OuResolutionTests
{
    [Theory]
    [InlineData("CN=PC-1,OU=Workstations,DC=corp,DC=local", "Workstations")]
    [InlineData("CN=PC-1,OU=Workstations,OU=Branch A,OU=Head Office,DC=corp,DC=local", "Head Office/Branch A/Workstations")]
    [InlineData("CN=PC-1,CN=Computers,DC=corp,DC=local", "Computers")]
    [InlineData("CN=PC-1,OU=Sales\\, North,DC=corp,DC=local", "Sales, North")]
    [InlineData("CN=PC-1,OU=Sales\\2C North,OU=A,DC=corp,DC=local", "A/Sales, North")]
    [InlineData("CN=PC-1,OU=Caf\\C3\\A9,DC=corp,DC=local", "Café")]
    [InlineData("CN=PC-1,OU=A, OU=B,DC=corp,DC=local", "B/A")]
    [InlineData("CN=PC-1,DC=corp,DC=local", null)]
    [InlineData("DC=corp,DC=local", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void OuFromDn_handles_paths_containers_escapes_and_empty_input(string? dn, string? expected) =>
        AdPaths.OuFromDn(dn).Should().Be(expected);

    [Fact]
    public void OuFromDn_keeps_deliberate_edge_spaces_inside_a_value() =>
        AdPaths.OuFromDn("CN=PC-1,OU=Lab\\20,DC=corp,DC=local").Should().Be("Lab ");

    private static OuIndex Index(params DirectoryOuEntry[] entries) => OuIndex.Create(entries);

    [Theory]
    [InlineData("pc-100")]
    [InlineData("PC-100.corp.local")]
    public void Matches_by_AD_name_case_insensitively_and_with_a_stored_FQDN(string host)
    {
        var index = Index(new DirectoryOuEntry("PC-100", "PC-100$", "pc-100.corp.local", "HQ/IT"));

        index.Resolve(host, null, out var ou).Should().Be(OuMatch.Found);
        ou.Should().Be("HQ/IT");
    }

    [Fact]
    public void Matches_by_sAMAccountName_without_the_trailing_dollar()
    {
        var index = Index(new DirectoryOuEntry("Display Name Differs", "wks-44$", null, "Branches/B"));

        index.Resolve("WKS-44", null, out var ou).Should().Be(OuMatch.Found);
        ou.Should().Be("Branches/B");
    }

    [Fact]
    public void Matches_by_the_first_label_of_dNSHostName()
    {
        var index = Index(new DirectoryOuEntry("Other", null, "Host-7.corp.local", "Computers"));

        index.Resolve("host-7", null, out var ou).Should().Be(OuMatch.Found);
        ou.Should().Be("Computers");
    }

    [Fact]
    public void Falls_back_to_the_reported_hostname_when_the_address_is_an_IP_or_unknown()
    {
        var index = Index(new DirectoryOuEntry("PC-5", null, null, "Floor 5"));

        index.Resolve("10.1.2.3", "pc-5.corp.local", out var ou).Should().Be(OuMatch.Found);
        ou.Should().Be("Floor 5");
        index.Resolve("no-such-pc", "pc-5", out var ou2).Should().Be(OuMatch.Found);
        ou2.Should().Be("Floor 5");
    }

    [Fact]
    public void An_IP_address_alone_is_never_matched_and_a_DC_only_object_is_not_found()
    {
        var index = Index(new DirectoryOuEntry("10", null, null, "Wrong"), new DirectoryOuEntry("PC-ROOT", null, null, null));

        index.Resolve("10.1.2.3", null, out _).Should().Be(OuMatch.NotFound);
        index.Resolve("PC-ROOT", null, out _).Should().Be(OuMatch.NotFound);
    }

    [Fact]
    public void Two_objects_with_one_key_prefer_the_one_whose_dNSHostName_equals_the_FQDN_else_the_first()
    {
        var index = Index(
            new DirectoryOuEntry("PC-9", null, "pc-9.old.local", "Old"),
            new DirectoryOuEntry("PC-9", null, "pc-9.corp.local", "New"));

        index.Resolve("PC-9.CORP.LOCAL", null, out var exact).Should().Be(OuMatch.Found);
        exact.Should().Be("New");
        index.Resolve("pc-9", null, out var first).Should().Be(OuMatch.FoundAmbiguous);
        first.Should().Be("Old");
    }

    [Fact]
    public void A_failed_result_is_typed_and_carries_no_map()
    {
        var r = OuLookupResult.Failed("The Active Directory query timed out.");

        r.Status.Should().Be(OuLookupStatus.Failed);
        r.Map.Should().BeEmpty();
        r.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Every_catalog_action_has_a_known_category_and_non_grid_ones_are_the_services_processes_profiles_set()
    {
        ActionCatalog.All.Should().OnlyContain(a => !string.IsNullOrEmpty(a.Category), "every action needs a category");
        ActionCatalog.All.Where(a => !ActionCategories.All.Contains(a.Category)).Select(a => a.Code).Should().BeEmpty("unknown category keys");
        ActionCatalog.All.Where(a => a.Code.StartsWith("svc.") && a.Code != "svc.spooler.reset").Should().OnlyContain(a => a.Category == ActionCategories.Services);
        ActionCatalog.Find("svc.spooler.reset")!.Category.Should().Be(ActionCategories.HealthRepair);
        ActionCatalog.Find("proc.kill")!.Category.Should().Be(ActionCategories.Processes);
        ActionCatalog.Find("profile.delete")!.Category.Should().Be(ActionCategories.Profiles);
        ActionCatalog.Find("script.run")!.Category.Should().Be(ActionCategories.AdminScripts);
        ActionCategories.Grid.Should().NotContain([ActionCategories.Services, ActionCategories.Processes, ActionCategories.Profiles]);
    }
}
