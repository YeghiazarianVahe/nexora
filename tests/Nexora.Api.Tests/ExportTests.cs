using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;

namespace Nexora.Api.Tests;

public sealed class ExportTests(NexoraFactory factory) : IClassFixture<NexoraFactory>
{
    private static readonly string Marker = "EXP" + Guid.NewGuid().ToString("N")[..6];

    private async Task<Guid> SeedAsync(Action<ManagedEndpoint> configure)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        var e = new ManagedEndpoint { Id = Guid.NewGuid(), Hostname = "H" + Guid.NewGuid().ToString("N")[..8], CreatedUtc = DateTime.UtcNow };
        configure(e);
        // The PC Name column is the host name (N7); these tests label rows by display name, so give both the same value.
        if (e.DisplayName is not null && e.ReportedHostname is null) e.Hostname = e.DisplayName;
        db.Endpoints.Add(e);
        await db.SaveChangesAsync();
        return e.Id;
    }

    private static async Task<(HttpResponseMessage Response, byte[] Bytes)> PostAsync(HttpClient c, object body, string url = "/api/endpoints/export")
    {
        var r = await c.PostAsJsonAsync(url, body);
        return (r, await r.Content.ReadAsByteArrayAsync());
    }

    /// <summary>Minimal RFC 4180 parser: quoted fields, doubled quotes, CRLF.</summary>
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cur = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cur.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cur.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cur.ToString()); cur.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { row.Add(cur.ToString()); cur.Clear(); rows.Add(row.ToArray()); row.Clear(); i++; }
            else cur.Append(c);
        }
        if (cur.Length > 0 || row.Count > 0) { row.Add(cur.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    private static List<string[]> Body(byte[] bytes) => ParseCsv(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));

    [Fact]
    public async Task Export_by_ids_returns_only_those_rows_in_the_requested_sort_order()
    {
        var a = await SeedAsync(e => e.DisplayName = $"{Marker}-LAB-2");
        var b = await SeedAsync(e => e.DisplayName = $"{Marker}-LAB-10");
        var c = await SeedAsync(e => e.DisplayName = $"{Marker}-LAB-1");
        var left = await SeedAsync(e => e.DisplayName = $"{Marker}-NOT-SELECTED");
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        var (response, bytes) = await PostAsync(viewer, new { ids = new[] { a, b, c }, sort = new { key = "name", dir = "desc" } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = Body(bytes);
        rows[0].Should().Equal(EndpointCsv.Header);
        rows.Skip(1).Select(r => r[0]).Should().Equal($"{Marker}-LAB-10", $"{Marker}-LAB-2", $"{Marker}-LAB-1"); // natural order, descending
        rows.Should().HaveCount(4).And.NotContain(r => r[0].Contains("NOT-SELECTED"));
        _ = left;

        var (_, asc) = await PostAsync(viewer, new { ids = new[] { a, b, c }, sort = new { key = "name", dir = "asc" } });
        Body(asc).Skip(1).Select(r => r[0]).Should().Equal($"{Marker}-LAB-1", $"{Marker}-LAB-2", $"{Marker}-LAB-10");
    }

    [Fact]
    public async Task Filter_mode_returns_the_same_workstations_as_the_list_endpoint_for_the_same_filter()
    {
        var tag = "PAR" + Guid.NewGuid().ToString("N")[..6];
        for (var i = 0; i < 4; i++) await SeedAsync(e => { e.DisplayName = $"{tag}-on-{i}"; e.Online = true; });
        for (var i = 0; i < 3; i++) await SeedAsync(e => { e.DisplayName = $"{tag}-off-{i}"; e.Online = false; });
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        var list = (await viewer.GetFromJsonAsync<List<EndpointSummaryDto>>($"/api/endpoints?search={tag}&online=true"))!;
        var (response, bytes) = await PostAsync(viewer, new { filter = new { search = tag, online = true } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var exported = Body(bytes).Skip(1).Select(r => r[0]).ToList();
        exported.Should().BeEquivalentTo(list.Select(l => l.Name));
        exported.Should().HaveCount(4);
        response.Content.Headers.ContentDisposition!.FileName.Should().StartWith("nexora-endpoints-filtered-");
    }

    [Fact]
    public async Task Extra_filter_fields_narrow_the_export()
    {
        var tag = "FLD" + Guid.NewGuid().ToString("N")[..6];
        await SeedAsync(e => { e.DisplayName = tag + "-dell"; e.Manufacturer = "Dell Inc."; e.Model = "Vostro 3030"; e.OrganizationalUnit = "Head Office/IT"; });
        await SeedAsync(e => { e.DisplayName = tag + "-hp"; e.Manufacturer = "HP"; e.Model = "t620"; e.OrganizationalUnit = "Branches/BranchA"; });
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        Body((await PostAsync(viewer, new { filter = new { search = tag, manufacturer = "Dell Inc." } })).Bytes).Should().HaveCount(2);
        Body((await PostAsync(viewer, new { filter = new { search = tag, ou = "Branches/BranchA" } })).Bytes)[1][0].Should().Be(tag + "-hp");
        Body((await PostAsync(viewer, new { filter = new { search = tag, model = "Vostro 3030" } })).Bytes)[1][0].Should().Be(tag + "-dell");
    }

    [Fact]
    public async Task A_body_with_both_or_neither_of_ids_and_filter_is_rejected()
    {
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        (await PostAsync(viewer, new { ids = new[] { Guid.NewGuid() }, filter = new { } })).Response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(viewer, new { sort = new { key = "name" } })).Response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(viewer, new { ids = Array.Empty<Guid>() })).Response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(viewer, new { filter = new { health = "bogus" } })).Response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task More_than_5000_ids_are_rejected_and_exactly_5000_are_accepted()
    {
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);
        var many = Enumerable.Range(0, 5001).Select(_ => Guid.NewGuid()).ToArray();

        (await PostAsync(viewer, new { ids = many })).Response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(viewer, new { ids = many.Take(5000).ToArray() })).Response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_ids_are_skipped_and_never_leak()
    {
        var real = await SeedAsync(e => e.DisplayName = Marker + "-real");
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        var (_, bytes) = await PostAsync(viewer, new { ids = new[] { real, Guid.NewGuid() } });

        Body(bytes).Skip(1).Select(r => r[0]).Should().Equal(Marker + "-real");
    }

    [Fact]
    public async Task Cells_that_could_run_as_formulas_are_neutralized()
    {
        var id = await SeedAsync(e =>
        {
            e.DisplayName = "=HYPERLINK(\"http://evil\",\"x\")";
            e.LastUser = "@SUM(1+1)";
            e.IpAddress = "-1";
        });
        var spaced = await SeedAsync(e => { e.DisplayName = " leading space"; e.LastUser = "\n=1"; });
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        var rows = Body((await PostAsync(viewer, new { ids = new[] { id, spaced }, sort = new { key = "name", dir = "asc" } })).Bytes);

        var evil = rows.Single(r => r[0].Contains("HYPERLINK"));
        evil[0].Should().Be("'=HYPERLINK(\"http://evil\",\"x\")");
        evil[1].Should().Be("'-1");
        evil[2].Should().Be("'@SUM(1+1)");
        var lead = rows.Single(r => r[0].Contains("leading space"));
        lead[0].Should().Be("' leading space");
        lead[2].Should().Be("'\n=1");
    }

    [Fact]
    public async Task The_file_has_a_bom_crlf_endings_and_armenian_and_russian_text_survives()
    {
        var id = await SeedAsync(e =>
        {
            e.DisplayName = "Աշխատանքային կայան, №3 \"Կ\"";
            e.LastUser = "CONTOSO\\Վահե Եղիազարյան";
        });
        var ru = await SeedAsync(e => { e.DisplayName = "Рабочая станция"; e.LastUser = "CONTOSO\\Иван Петров"; });
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        var (response, bytes) = await PostAsync(viewer, new { ids = new[] { id, ru }, sort = new { key = "name", dir = "asc" } });

        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        var text = Encoding.UTF8.GetString(bytes);
        text.Should().Contain("\r\n").And.NotContain("\n\n");
        text.Replace("\r\n", "").Should().NotContain("\n", "every line break is CRLF");
        response.Content.Headers.ContentType!.ToString().Should().StartWith("text/csv");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");

        var rows = Body(bytes);
        rows.Should().Contain(r => r[0] == "Աշխատանքային կայան, №3 \"Կ\"" && r[2] == "CONTOSO\\Վահե Եղիազարյան");
        rows.Should().Contain(r => r[0] == "Рабочая станция" && r[2] == "CONTOSO\\Иван Петров");
    }

    [Fact]
    public async Task Values_are_whole_numbers_and_Last_Logon_is_Yerevan_time_in_the_requested_format()
    {
        var seen = new DateTime(2026, 10, 12, 11, 44, 6, DateTimeKind.Utc);
        var id = await SeedAsync(e =>
        {
            e.DisplayName = "PC-NUM"; e.IpAddress = "10.1.2.3"; e.Online = true; e.LastUser = "CONTOSO\\user";
            e.CpuPercent = 12.5; e.RamPercent = 74.3; e.LastSeenUtc = seen;
        });
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        var row = Body((await PostAsync(viewer, new { ids = new[] { id } })).Bytes)[1];

        row.Should().HaveCount(10);
        (row[0], row[1], row[2]).Should().Be(("PC-NUM", "10.1.2.3", "CONTOSO\\user"));
        row[3].Should().Be("12.10.2026 15:44");            // dd.MM.yyyy HH:mm, Asia/Yerevan (UTC+4)
        (row[4], row[5]).Should().Be(("13", "74"));         // whole numbers, half away from zero
        row.Should().OnlyContain(c => !c.Contains('%'));
    }

    [Fact]
    public async Task Each_export_is_audited_with_its_mode_and_row_count_but_a_preview_is_not()
    {
        var sid = TestIdentities.NewSid();
        var viewer = await factory.UserClientAsync(sid, Role.Viewer, "TEST\\auditor");
        var one = await SeedAsync(e => e.DisplayName = Marker + "-audit");

        await PostAsync(viewer, new { ids = new[] { one } });
        await PostAsync(viewer, new { filter = new { search = Marker + "-audit" } });
        await PostAsync(viewer, new { filter = new { } });
        await PostAsync(viewer, new { filter = new { } }, "/api/endpoints/export?preview=true");

        var entries = factory.Query(db => db.AuditEntries.Where(a => a.ActorSid == sid && a.Action == "endpoint.export").OrderBy(a => a.Id).ToList());
        entries.Select(e => e.NewValue).Should().Equal("selected", "filtered", "all");
        entries[0].Details.Should().Contain("\"count\":1");
        entries.Should().OnlyContain(e => e.ActorAccount == "TEST\\auditor" && e.TimestampUtc > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Preview_returns_the_mode_and_count_as_json()
    {
        var tag = "PRV" + Guid.NewGuid().ToString("N")[..6];
        await SeedAsync(e => e.DisplayName = tag + "-1");
        await SeedAsync(e => e.DisplayName = tag + "-2");
        var viewer = await factory.UserClientAsync(TestIdentities.NewSid(), Role.Viewer);

        var response = await viewer.PostAsJsonAsync("/api/endpoints/export?preview=true", new { filter = new { search = tag } });

        var json = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())!;
        (json.GetProperty("mode").GetString(), json.GetProperty("count").GetInt32()).Should().Be(("filtered", 2));
    }

    [Fact]
    public async Task Anonymous_callers_are_challenged_and_role_less_users_are_refused()
    {
        (await factory.ClientFor(null).PostAsJsonAsync("/api/endpoints/export", new { filter = new { } })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var noRole = await factory.UserClientAsync(TestIdentities.NewSid(), role: null);
        (await noRole.PostAsJsonAsync("/api/endpoints/export", new { filter = new { } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Exports_need_the_anti_forgery_token_like_every_other_write()
    {
        var viewer = factory.ClientFor(TestIdentities.NewSid());
        await factory.SeedUserAsync(TestIdentities.NewSid(), Role.Viewer);
        (await viewer.PostAsJsonAsync("/api/endpoints/export", new { filter = new { } })).StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden);
    }
}

public sealed class CsvUnitTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@x", "'@x")]
    [InlineData("a=b", "a=b")]
    [InlineData(" =1", "'" + " =1")]
    [InlineData(" lead", "'" + " lead")]
    [InlineData("\n=1", "\"'\n=1\"")]
    [InlineData("\t1", "'\t1")]
    [InlineData("", "")]
    public void Text_cells_are_quoted_and_neutralized(string input, string expected) => EndpointCsv.Text(input).Should().Be(expected);

    [Fact]
    public void File_names_state_the_mode_and_use_Yerevan_time()
    {
        EndpointCsv.FileName("selected", new DateTime(2026, 10, 12, 11, 5, 0, DateTimeKind.Utc)).Should().Be("nexora-endpoints-selected-20261012-1505.csv");
    }
}
