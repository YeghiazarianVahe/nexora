using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Services;

namespace Nexora.Api.Tests;

/// <summary>N7: the CSV columns say what they hold.</summary>
public sealed class CsvHonestyTests
{
    private static EndpointSummaryDto Pc(string host, string name, string? reported) =>
        new(Guid.NewGuid(), host, name, reported, null, null, null, null, null, null, true, null, null, null, [], null, null, []);

    [Fact]
    public void The_PC_name_is_the_host_name_not_the_display_name()
    {
        EndpointCsv.HostName(Pc("10.0.0.5", "Cashier 3", "WKS-44")).Should().Be("WKS-44", "the name the PC reported about itself");
        EndpointCsv.HostName(Pc("wks-50", "Cashier 3", null)).Should().Be("wks-50", "else the name Nexora reaches it by");
        EndpointCsv.HostName(Pc("wks-50", "Cashier 3", "  ")).Should().Be("wks-50");
    }

    [Fact]
    public void There_is_no_column_called_Last_Logon_because_the_poll_does_not_record_one()
    {
        EndpointCsv.Header.Should().NotContain("Last Logon").And.Contain("Last Seen Online");
        EndpointCsv.Header.Should().HaveCount(10);
    }
}
