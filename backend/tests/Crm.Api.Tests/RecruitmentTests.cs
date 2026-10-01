using System.Net;
using System.Text.Json.Nodes;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Features.Customization;
using Crm.Api.Features.Import;
using Crm.Api.Features.Records;
using Crm.Api.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crm.Api.Tests;

public class RecruitmentTests(CrmApiFactory factory) : IClassFixture<CrmApiFactory>
{
    private const string Password = "Ats#Pass12345";

    private async Task<(HttpClient Client, Guid TenantId)> RecruitmentTenantAsync(bool demo = false)
    {
        var platform = await factory.CreateAuthenticatedClientAsync();
        var slug = $"rec-{Guid.NewGuid():N}"[..20];
        var email = $"admin@{slug}.test";
        var created = await (await platform.PostJsonAsync("/api/platform/tenants", new
        {
            name = "Talent Co", slug, template = "recruitment", currency = "AUD",
            adminEmail = email, adminFirstName = "Ria", adminLastName = "Recruiter", adminPassword = Password, seedDemoData = demo,
        })).ReadAsync<TenantItem>();
        return (await factory.CreateAuthenticatedClientAsync(email, Password), created.Id);
    }

    private static async Task<List<RecordItem>> RecordsAsync(HttpClient client, string objectKey, string query = "") =>
        [.. (await (await client.GetAsync($"/api/records/{objectKey}?pageSize=100{query}")).ReadAsync<PagedResult<RecordItem>>()).Items];

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string objectKey, string name, object customFields) =>
        client.PostJsonAsync($"/api/records/{objectKey}", new { name, customFields });

    [Fact]
    public async Task Recruitment_template_has_a_talent_pool_and_applications_link_candidates_to_jobs()
    {
        var (ats, _) = await RecruitmentTenantAsync(demo: true);
        var meta = await (await ats.GetAsync("/api/metadata")).ReadAsync<MetadataDto>();
        var candidate = meta.Objects.Single(o => o.Key == "candidate");
        Assert.True(candidate.Fields.Single(f => f.Key == "email").IsUnique);
        var link = meta.Objects.Single(o => o.Key == "application").Fields.Single(f => f.Key == "candidate");
        Assert.True(link.IsRequired);
        Assert.Equal("candidate", link.LookupObjectKey);

        var jobs = await RecordsAsync(ats, "position");
        var people = await RecordsAsync(ats, "candidate");
        var applications = await RecordsAsync(ats, "application");
        Assert.Equal(6, jobs.Count);
        Assert.Equal(16, people.Count);
        Assert.Equal(22, applications.Count);
        Assert.Contains(jobs, j => j.Name == "Senior Backend Engineer");
        Assert.Equal(people.Count, people.Select(p => p.CustomFields["email"]!.GetValue<string>()).Distinct().Count());
        Assert.All(applications, a =>
        {
            var person = people.Single(p => p.Id.ToString() == a.CustomFields["candidate"]!.GetValue<string>());
            Assert.Equal(person.Name, a.Name);
            Assert.Contains(jobs, j => j.Id.ToString() == a.CustomFields["position"]!.GetValue<string>());
        });
        Assert.True(applications.Select(a => a.Stage).Distinct().Count() > 2);
    }

    [Fact]
    public async Task Unique_fields_block_duplicates_in_forms_imports_and_settings()
    {
        var (ats, _) = await RecruitmentTenantAsync();
        var jane = await (await CreateAsync(ats, "candidate", "Jane Doe", new { email = " Jane.Doe@Example.com " })).ReadAsync<RecordItem>();
        Assert.Equal("jane.doe@example.com", jane.CustomFields["email"]!.GetValue<string>());

        var duplicate = await CreateAsync(ats, "candidate", "Jane D.", new { email = "JANE.DOE@example.com" });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Contains("Another record already has this email", await duplicate.Content.ReadAsStringAsync());

        var resave = await ats.PutJsonAsync($"/api/records/candidate/{jane.Id}", new { name = "Jane Doe", customFields = new { email = "jane.doe@example.com", location = "Sydney" } });
        Assert.Equal(HttpStatusCode.OK, resave.StatusCode);

        var mapping = new Dictionary<string, string?> { ["Name"] = "name", ["Email"] = "cf.email" };
        var import = await (await ats.PostJsonAsync("/api/import/candidate", new
        {
            csv = "Name,Email\nRaj Patel,raj@example.com\nJane Again,jane.doe@EXAMPLE.com\nRaj Twin,RAJ@example.com\n", mapping, dryRun = true,
        })).ReadAsync<ImportResult>();
        Assert.Equal(2, import.ErrorCount);
        Assert.Contains(import.Errors.Single(e => e.Row == 3).Messages, m => m.Contains("Another record already has this email"));
        Assert.Contains(import.Errors.Single(e => e.Row == 4).Messages, m => m.Contains("same value as row 2"));

        (await CreateAsync(ats, "candidate", "Sam One", new { location = "Perth" })).EnsureSuccessStatusCode();
        (await CreateAsync(ats, "candidate", "Sam Two", new { location = "Perth" })).EnsureSuccessStatusCode();
        var location = (await (await ats.GetAsync("/api/metadata")).ReadAsync<MetadataDto>())
            .Objects.Single(o => o.Key == "candidate").Fields.Single(f => f.Key == "location");
        var makeUnique = await ats.PutJsonAsync("/api/admin/objects/candidate/fields/location", location with { IsUnique = true });
        Assert.Equal(HttpStatusCode.BadRequest, makeUnique.StatusCode);
        Assert.Contains("records share a value", await makeUnique.Content.ReadAsStringAsync());
        var numberField = new { key = "score", label = "Score", type = "Number", isRequired = false, showInList = false, sortOrder = 20, isUnique = true };
        Assert.Equal(HttpStatusCode.BadRequest, (await ats.PostJsonAsync("/api/admin/objects/candidate/fields", numberField)).StatusCode);
    }

    [Fact]
    public async Task Boards_and_lists_filter_by_linked_job_or_candidate()
    {
        var (ats, _) = await RecruitmentTenantAsync(demo: true);
        var applications = await RecordsAsync(ats, "application");
        var jobId = applications.GroupBy(a => a.CustomFields["position"]!.GetValue<string>()).OrderByDescending(g => g.Count()).First().Key;
        var expected = applications.Where(a => a.CustomFields["position"]!.GetValue<string>() == jobId).Select(a => a.Id).ToHashSet();

        var board = await (await ats.GetAsync($"/api/records/application/board?cf.position={jobId}")).ReadAsync<List<RecordItem>>();
        Assert.NotEmpty(board);
        Assert.All(board, r => Assert.Contains(r.Id, expected));

        var list = await RecordsAsync(ats, "application", $"&cf.position={jobId}");
        Assert.Equal(expected, list.Select(r => r.Id).ToHashSet());

        var candidateId = applications[0].CustomFields["candidate"]!.GetValue<string>();
        Assert.All(await RecordsAsync(ats, "application", $"&cf.candidate={candidateId}"),
            r => Assert.Equal(candidateId, r.CustomFields["candidate"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Older_recruitment_tenants_are_upgraded_once_without_touching_their_data()
    {
        var (ats, tenantId) = await RecruitmentTenantAsync();
        var job = await (await CreateAsync(ats, "position", "Legacy Role", new { status = "Open" })).ReadAsync<RecordItem>();

        // Rewind the tenant to template v1: no candidate object, and an application without a candidate.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
            await db.FieldDefinitions.IgnoreQueryFilters().Where(f => f.TenantId == tenantId && (f.ObjectKey == "candidate" || f.Key == "candidate")).ExecuteDeleteAsync();
            await db.ObjectDefinitions.IgnoreQueryFilters().Where(o => o.TenantId == tenantId && o.Key == "candidate").ExecuteDeleteAsync();
            await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.TemplateVersion, 1));
        }
        var legacy = await (await CreateAsync(ats, "application", "Old Applicant", new { position = job.Id.ToString() })).ReadAsync<RecordItem>();

        await UpgradeAsync();
        var meta = await (await ats.GetAsync("/api/metadata")).ReadAsync<MetadataDto>();
        Assert.Contains(meta.Objects, o => o.Key == "candidate");
        Assert.False(meta.Objects.Single(o => o.Key == "application").Fields.Single(f => f.Key == "candidate").IsRequired);
        var after = await (await ats.GetAsync($"/api/records/application/{legacy.Id}")).ReadAsync<RecordItem>();
        Assert.Equal(legacy.CustomFields.ToJsonString(), after.CustomFields.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, (await ats.PutJsonAsync($"/api/records/application/{legacy.Id}",
            new { name = "Old Applicant", customFields = new { rating = 4 } })).StatusCode);

        // An admin removing the object afterwards is respected: the upgrade doesn't run again.
        Assert.Equal(HttpStatusCode.NoContent, (await ats.DeleteAsync("/api/admin/objects/candidate")).StatusCode);
        await UpgradeAsync();
        Assert.DoesNotContain((await (await ats.GetAsync("/api/metadata")).ReadAsync<MetadataDto>()).Objects, o => o.Key == "candidate");

        var admin = await factory.CreateAuthenticatedClientAsync();
        Assert.DoesNotContain((await (await admin.GetAsync("/api/metadata")).ReadAsync<MetadataDto>()).Objects, o => o.Key == "candidate");
    }

    private async Task UpgradeAsync()
    {
        using var scope = factory.Services.CreateScope();
        await TenantTemplates.UpgradeTenantsAsync(scope.ServiceProvider.GetRequiredService<CrmDbContext>(),
            scope.ServiceProvider.GetRequiredService<TenantContext>(), NullLogger.Instance);
    }
}
