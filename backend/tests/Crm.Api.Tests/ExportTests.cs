using System.Net;
using System.Text;
using Crm.Api.Common;
using Crm.Api.Domain;
using Crm.Api.Features.Export;
using Crm.Api.Features.Governance;
using Crm.Api.Features.Import;
using Crm.Api.Features.Records;
using Crm.Api.Features.Security;
using Crm.Api.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Api.Tests;

public class ExportTests(CrmApiFactory factory) : IClassFixture<CrmApiFactory>
{
    private const string Password = "Ats#Pass12345";

    private async Task<HttpClient> RecruitmentTenantAsync()
    {
        var platform = await factory.CreateAuthenticatedClientAsync();
        var slug = $"exp-{Guid.NewGuid():N}"[..20];
        var email = $"admin@{slug}.test";
        (await platform.PostJsonAsync("/api/platform/tenants", new
        {
            name = "Export Co", slug, template = "recruitment", currency = "AUD",
            adminEmail = email, adminFirstName = "Eve", adminLastName = "Exporter", adminPassword = Password, seedDemoData = true,
        })).EnsureSuccessStatusCode();
        return await factory.CreateAuthenticatedClientAsync(email, Password);
    }

    /// <summary>Downloads an export and returns its rows as header-keyed dictionaries.</summary>
    private static async Task<List<Dictionary<string, string>>> ExportAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/csv", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var rows = Csv.Parse(Encoding.UTF8.GetString(bytes));
        var headers = rows[0].Cells;
        return rows.Skip(1).Select(r => headers.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => r.Cells[x.i])).ToList();
    }

    [Fact]
    public void Cells_are_quoted_and_formulas_are_neutralised()
    {
        Assert.Equal("\"'=HYPERLINK(\"\"x\"\")\"", ExportEndpoints.Cell("=HYPERLINK(\"x\")", true));
        Assert.Equal("'@SUM(A1)", ExportEndpoints.Cell("@SUM(A1)", true));
        Assert.Equal("'+1", ExportEndpoints.Cell("+1", true));
        Assert.Equal("-5", ExportEndpoints.Cell("-5", false));
        Assert.Equal("\"a,b\"", ExportEndpoints.Cell("a,b", true));
        Assert.Equal("\"say \"\"hi\"\"\"", ExportEndpoints.Cell("say \"hi\"", true));
        Assert.Equal("\"two\nlines\"", ExportEndpoints.Cell("two\nlines", true));
        Assert.Equal("plain", ExportEndpoints.Cell("plain", true));
    }

    [Fact]
    public async Task Exports_follow_list_filters_and_permissions_show_names_and_are_audited()
    {
        var ats = await RecruitmentTenantAsync();
        var applications = (await (await ats.GetAsync("/api/records/application?pageSize=100")).ReadAsync<PagedResult<RecordItem>>()).Items;
        var jobId = applications.GroupBy(a => a.CustomFields["position"]!.GetValue<string>()).OrderByDescending(g => g.Count()).First().Key;
        var jobs = (await (await ats.GetAsync("/api/records/position?pageSize=100")).ReadAsync<PagedResult<RecordItem>>()).Items;
        var jobTitle = jobs.Single(j => j.Id.ToString() == jobId).Name;
        var expected = applications.Where(a => a.CustomFields["position"]!.GetValue<string>() == jobId).ToList();

        var rows = await ExportAsync(ats, $"/api/export/application?cf.position={jobId}");
        Assert.Equal(expected.Select(a => a.Id.ToString()).Order(), rows.Select(r => r["Id"]).Order());
        Assert.All(rows, r => Assert.Equal(jobTitle, r["Job opening"]));
        var first = expected[0];
        var row = rows.Single(r => r["Id"] == first.Id.ToString());
        Assert.Equal(first.Name, row["Candidate name"]);
        Assert.Equal(first.Name, row["Candidate"]);
        Assert.Contains(row["Stage"], new[] { "Applied", "Screening", "Interview", "Offer", "Hired", "Rejected" });
        Assert.NotEmpty(row["Owner"]);

        var open = await ExportAsync(ats, "/api/export/application?open=true&search=" + Uri.EscapeDataString(first.Name[..3]));
        Assert.All(open, r => Assert.Contains(first.Name[..3], r["Candidate name"], StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(open, r => r["Stage"] is "Hired" or "Rejected");

        var audit = await (await ats.GetAsync("/api/admin/audit?action=Exported")).ReadAsync<PagedResult<AuditItem>>();
        Assert.Contains(audit.Items, a => a.EntityType == "application" && a.EntityName == $"{rows.Count} applications"
                                          && a.Changes["filters"]?["cf.position"]?.GetValue<string>() == jobId);

        var role = await (await ats.PostJsonAsync("/api/admin/roles", new
        {
            name = "Own reader", description = (string?)null,
            permissions = new[] { new { resource = "*", action = "read", scope = "Own" } },
        })).ReadAsync<RoleItem>();
        var reader = await factory.CreateUserClientAsyncIn(ats, role.Name);
        Assert.Empty(await ExportAsync(reader, "/api/export/application"));
        var standardUser = await factory.CreateUserClientAsyncIn(ats, "Standard User");
        var deny = await (await ats.PostJsonAsync("/api/admin/roles", new
        {
            name = "No candidates", description = (string?)null,
            permissions = new[] { new { resource = "candidate", action = "read", scope = "None" } },
        })).ReadAsync<RoleItem>();
        Assert.Equal(HttpStatusCode.Forbidden, (await (await factory.CreateUserClientAsyncIn(ats, deny.Name)).GetAsync("/api/export/candidate")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await standardUser.GetAsync("/api/export/candidate")).StatusCode);

        (await ats.PostJsonAsync("/api/records/candidate", new { name = "=cmd|' /C calc'!A0", customFields = new { } })).EnsureSuccessStatusCode();
        var csv = await (await ats.GetAsync("/api/export/candidate")).Content.ReadAsStringAsync();
        Assert.Contains("'=cmd|' /C calc'!A0", csv);
        Assert.DoesNotContain(",=cmd", csv);
        Assert.Equal(HttpStatusCode.NotFound, (await ats.GetAsync("/api/export/nothing")).StatusCode);
    }

    [Fact]
    public async Task Standard_objects_export_with_their_own_filters_and_large_exports_are_refused()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        var tag = Guid.NewGuid().ToString("N")[..6];
        var account = await (await admin.PostJsonAsync("/api/accounts", new { name = $"Export {tag} Pty", industry = "Retail" })).ReadAsync<AccountRef>();
        (await admin.PostJsonAsync("/api/accounts", new { name = $"Other {tag} Pty", industry = "Software" })).EnsureSuccessStatusCode();
        (await admin.PostJsonAsync("/api/contacts", new { firstName = "Cleo", lastName = $"Tag{tag}", accountId = account.Id })).EnsureSuccessStatusCode();
        (await admin.PostJsonAsync("/api/leads", new { firstName = "Lee", lastName = $"Q{tag}", company = "Q Co", source = "Web", status = "Qualified" })).EnsureSuccessStatusCode();
        (await admin.PostJsonAsync("/api/leads", new { firstName = "Lee", lastName = $"N{tag}", company = "N Co", source = "Web", status = "New" })).EnsureSuccessStatusCode();

        var contacts = await ExportAsync(admin, $"/api/export/contact?accountId={account.Id}");
        var cleo = Assert.Single(contacts);
        Assert.Equal(($"Export {tag} Pty", "Cleo", $"Tag{tag}"), (cleo["Account"], cleo["First name"], cleo["Last name"]));

        var retail = await ExportAsync(admin, $"/api/export/account?industry=Retail&search={tag}");
        Assert.Equal([$"Export {tag} Pty"], retail.Select(r => r["Name"]));
        var qualified = await ExportAsync(admin, $"/api/export/lead?status=Qualified&search={tag}");
        Assert.Equal(["Qualified"], qualified.Select(r => r["Status"]));

        var config = factory.Services.GetRequiredService<IConfiguration>();
        config["Export:MaxRows"] = "1";
        try
        {
            var tooMany = await admin.GetAsync($"/api/export/account?search={tag}");
            Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
            Assert.Contains("at most 1", await tooMany.Content.ReadAsStringAsync());
        }
        finally
        {
            config["Export:MaxRows"] = null;
        }
    }

    private sealed record AccountRef(Guid Id);
}
