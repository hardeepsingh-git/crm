using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Features.Activities;
using Crm.Api.Features.Attachments;
using Crm.Api.Features.Careers;
using Crm.Api.Features.Customization;
using Crm.Api.Features.Email;
using Crm.Api.Features.Governance;
using Crm.Api.Features.Records;
using Crm.Api.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Api.Tests;

public class CareersTests(CrmApiFactory factory) : IClassFixture<CrmApiFactory>
{
    private const string Password = "Ats#Pass12345";
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% test CV\n");

    private sealed record Org(HttpClient Admin, Guid Id, string Slug);

    private async Task<Org> RecruitmentTenantAsync(bool careers = true)
    {
        var platform = await factory.CreateAuthenticatedClientAsync();
        var slug = $"jobs-{Guid.NewGuid():N}"[..20];
        var email = $"admin@{slug}.test";
        var created = await (await platform.PostJsonAsync("/api/platform/tenants", new
        {
            name = "Talent Co", slug, template = "recruitment", currency = "AUD",
            adminEmail = email, adminFirstName = "Ria", adminLastName = "Recruiter", adminPassword = Password, seedDemoData = true,
        })).ReadAsync<TenantItem>();
        var admin = await factory.CreateAuthenticatedClientAsync(email, Password);
        if (careers)
            (await admin.PutJsonAsync("/api/settings", new { name = "Talent Co", currency = "AUD", careersEnabled = true, careersIntro = "Join us." }))
                .EnsureSuccessStatusCode();
        return new Org(admin, created.Id, slug);
    }

    private static MultipartFormDataContent Form(
        string name = "Nina Patel", string email = "Nina.Patel@Example.com", string? phone = "+61 400 000 001", bool consent = true,
        string? linkedin = "https://www.linkedin.com/in/nina", string? coverNote = "I love data.", byte[]? cv = null, string fileName = "cv.pdf",
        string? website = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(name), "name" },
            { new StringContent(email), "email" },
            { new StringContent(consent ? "true" : "false"), "consent" },
        };
        if (phone is not null) form.Add(new StringContent(phone), "phone");
        if (linkedin is not null) form.Add(new StringContent(linkedin), "linkedin");
        if (coverNote is not null) form.Add(new StringContent(coverNote), "coverNote");
        if (website is not null) form.Add(new StringContent(website), "website");
        if (cv is not { Length: 0 })
        {
            var file = new ByteArrayContent(cv ?? Pdf);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "cv", fileName);
        }
        return form;
    }

    private static Task<HttpResponseMessage> ApplyAsync(HttpClient client, string slug, Guid jobId, MultipartFormDataContent form) =>
        client.PostAsync($"/api/careers/{slug}/jobs/{jobId}/apply", form);

    private static async Task<List<RecordItem>> RecordsAsync(HttpClient client, string objectKey, string query = "") =>
        [.. (await (await client.GetAsync($"/api/records/{objectKey}?pageSize=100{query}")).ReadAsync<PagedResult<RecordItem>>()).Items];

    [Fact]
    public async Task Careers_page_is_off_until_enabled_and_lists_only_published_open_jobs()
    {
        var anonymous = factory.CreateClient();
        var org = await RecruitmentTenantAsync(careers: false);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/careers/{org.Slug}")).StatusCode);

        var settings = await (await org.Admin.PutJsonAsync("/api/settings", new { name = "Talent Co", currency = "AUD", careersEnabled = true, careersIntro = "  Join us.  " }))
            .ReadAsync<OrganisationSettings>();
        Assert.True(settings.CareersEnabled && settings.CareersAvailable);
        Assert.Equal("Join us.", settings.CareersIntro);
        var sales = await factory.CreateAuthenticatedClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await sales.PutJsonAsync("/api/settings", new { name = "Default", currency = "AUD", careersEnabled = true })).StatusCode);

        var page = await (await anonymous.GetAsync($"/api/careers/{org.Slug.ToUpperInvariant()}")).ReadAsync<CareersPage>();
        Assert.Equal(("Talent Co", "Join us."), (page.Organisation, page.Intro));
        Assert.Equal(4, page.Jobs.Count);
        Assert.DoesNotContain("salary", await (await anonymous.GetAsync($"/api/careers/{org.Slug}")).Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var jobs = await RecordsAsync(org.Admin, "position");
        RecordItem Job(Guid id) => jobs.Single(j => j.Id == id);
        var hidden = Job(page.Jobs[0].Id);
        (await org.Admin.PutJsonAsync($"/api/records/position/{hidden.Id}", new { name = hidden.Name, customFields = new { publish = false } })).EnsureSuccessStatusCode();
        var closed = Job(page.Jobs[1].Id);
        (await org.Admin.PutJsonAsync($"/api/records/position/{closed.Id}", new
        {
            name = closed.Name, customFields = new { closing_date = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd") },
        })).EnsureSuccessStatusCode();
        var unpublished = jobs.Single(j => j.CustomFields["publish"]?.GetValue<bool>() != true && j.CustomFields["status"]!.GetValue<string>() == "Open");

        var after = await (await anonymous.GetAsync($"/api/careers/{org.Slug}")).ReadAsync<CareersPage>();
        Assert.Equal(2, after.Jobs.Count);
        Assert.DoesNotContain(after.Jobs, j => j.Id == hidden.Id || j.Id == closed.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/careers/{org.Slug}/jobs/{hidden.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/careers/{org.Slug}/jobs/{unpublished.Id}")).StatusCode);
        var detail = await (await anonymous.GetAsync($"/api/careers/{org.Slug}/jobs/{after.Jobs[0].Id}")).ReadAsync<CareerJobDetail>();
        Assert.Contains("We're looking for", detail.Description);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/careers/no-such-org")).StatusCode);
    }

    [Fact]
    public async Task Applying_creates_the_candidate_and_application_attaches_the_cv_and_tells_the_recruiter()
    {
        var org = await RecruitmentTenantAsync();
        var ack = (await (await org.Admin.GetAsync("/api/admin/automation")).ReadAsync<List<RuleItem>>()).Single(r => r.Name == "Acknowledge new applications");
        (await org.Admin.PutJsonAsync($"/api/admin/automation/{ack.Id}", ack with { IsActive = true })).EnsureSuccessStatusCode();
        var anonymous = factory.CreateClient();
        var job = (await (await anonymous.GetAsync($"/api/careers/{org.Slug}")).ReadAsync<CareersPage>()).Jobs[0];

        var response = await ApplyAsync(anonymous, org.Slug, job.Id, Form());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(CareersEndpoints.Thanks, (await response.ReadAsync<ApplicationReceived>()).Message);

        var candidate = Assert.Single(await RecordsAsync(org.Admin, "candidate", "&cf.email=nina.patel@example.com"));
        Assert.Equal("Nina Patel", candidate.Name);
        Assert.Equal("Website", candidate.CustomFields["source"]!.GetValue<string>());
        Assert.Equal("https://www.linkedin.com/in/nina", candidate.CustomFields["linkedin"]!.GetValue<string>());
        Assert.NotNull(candidate.CustomFields["privacy_consent"]);
        var jobRecord = (await RecordsAsync(org.Admin, "position")).Single(j => j.Id == job.Id);
        Assert.Equal(jobRecord.OwnerId, candidate.OwnerId);

        var application = Assert.Single(await RecordsAsync(org.Admin, "application", $"&cf.candidate={candidate.Id}"));
        Assert.Equal(("Nina Patel", "applied", job.Id.ToString(), jobRecord.OwnerId),
            (application.Name, application.Stage, application.CustomFields["position"]!.GetValue<string>(), application.OwnerId));
        Assert.Equal("I love data.", application.CustomFields["cover_note"]!.GetValue<string>());

        foreach (var (key, id) in new[] { ("candidate", candidate.Id), ("application", application.Id) })
        {
            var file = Assert.Single(await (await org.Admin.GetAsync($"/api/attachments?objectKey={key}&recordId={id}")).ReadAsync<List<AttachmentItem>>());
            Assert.Equal(("cv.pdf", "application/pdf", CareersEndpoints.Actor), (file.FileName, file.ContentType, file.UploadedByName));
            Assert.Equal(Pdf, await (await org.Admin.GetAsync($"/api/attachments/{file.Id}/content")).Content.ReadAsByteArrayAsync());
        }
        var note = Assert.Single((await (await org.Admin.GetAsync($"/api/activities?customRecordId={application.Id}")).ReadAsync<PagedResult<ActivityItem>>()).Items,
            a => a.Subject == "Applied on the careers page");
        Assert.Contains("Phone: +61 400 000 001", note.Description);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        Assert.True(await db.Notifications.IgnoreQueryFilters().AnyAsync(n =>
            n.TenantId == org.Id && n.UserId == jobRecord.OwnerId && n.Title == "New application" && n.Link == $"/o/application/{application.Id}"));
        Assert.True(await db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.EntityId == application.Id && a.UserName == CareersEndpoints.Actor));
        var acknowledgement = Assert.Single((await (await org.Admin.GetAsync("/api/admin/emails")).ReadAsync<EmailLogPage>()).Page.Items);
        Assert.Equal(("nina.patel@example.com", $"Your application for {job.Title}"), (acknowledgement.To, acknowledgement.Subject));
    }

    [Fact]
    public async Task Reapplying_never_changes_existing_candidates_or_duplicates_applications()
    {
        var org = await RecruitmentTenantAsync();
        var anonymous = factory.CreateClient();
        var jobs = (await (await anonymous.GetAsync($"/api/careers/{org.Slug}")).ReadAsync<CareersPage>()).Jobs;
        var omar = await (await org.Admin.PostJsonAsync("/api/records/candidate", new
        {
            name = "Omar Haddad", customFields = new { email = "omar@example.com", phone = "111" },
        })).ReadAsync<RecordItem>();

        var first = await ApplyAsync(anonymous, org.Slug, jobs[0].Id, Form(name: "Someone Else", email: "OMAR@example.com", phone: "999"));
        var again = await ApplyAsync(anonymous, org.Slug, jobs[0].Id, Form(name: "Someone Else", email: "omar@example.com", phone: "999"));
        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.Accepted), (first.StatusCode, again.StatusCode));
        Assert.Equal(await first.Content.ReadAsStringAsync(), await again.Content.ReadAsStringAsync());

        var stored = Assert.Single(await RecordsAsync(org.Admin, "candidate", "&cf.email=omar@example.com"));
        Assert.Equal(("Omar Haddad", "111", omar.Id), (stored.Name, stored.CustomFields["phone"]!.GetValue<string>(), stored.Id));
        var application = Assert.Single(await RecordsAsync(org.Admin, "application", $"&cf.candidate={omar.Id}"));
        Assert.Equal("Omar Haddad", application.Name);
        var notes = (await (await org.Admin.GetAsync($"/api/activities?customRecordId={application.Id}")).ReadAsync<PagedResult<ActivityItem>>()).Items;
        Assert.Contains(notes, n => n.Subject == "Applied again on the careers page" && n.Description!.Contains("Phone: 999"));
        Assert.Equal(2, (await (await org.Admin.GetAsync($"/api/attachments?objectKey=application&recordId={application.Id}")).ReadAsync<List<AttachmentItem>>()).Count);

        (await ApplyAsync(anonymous, org.Slug, jobs[1].Id, Form(email: "omar@example.com"))).EnsureSuccessStatusCode();
        Assert.Equal(2, (await RecordsAsync(org.Admin, "application", $"&cf.candidate={omar.Id}")).Count);
        Assert.Single(await RecordsAsync(org.Admin, "candidate", "&cf.email=omar@example.com"));
    }

    [Fact]
    public async Task Invalid_or_abusive_submissions_are_refused()
    {
        var org = await RecruitmentTenantAsync();
        var anonymous = factory.CreateClient();
        var job = (await (await anonymous.GetAsync($"/api/careers/{org.Slug}")).ReadAsync<CareersPage>()).Jobs[0];

        async Task Refused(MultipartFormDataContent form, string field)
        {
            var response = await ApplyAsync(anonymous, org.Slug, job.Id, form);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync());
        }
        await Refused(Form(consent: false), "consent");
        await Refused(Form(email: "not-an-email"), "email");
        await Refused(Form(email: "a@example.com, b@example.com"), "email");
        await Refused(Form(linkedin: "javascript:alert(1)"), "linkedin");
        await Refused(Form(cv: []), "cv");
        await Refused(Form(fileName: "cv.exe"), "cv");
        await Refused(Form(cv: Encoding.ASCII.GetBytes("MZ\x90\0 renamed program"), fileName: "cv.pdf"), "cv");
        var config = factory.Services.GetRequiredService<IConfiguration>();
        config["Careers:MaxCvSizeMb"] = "1";
        try
        {
            await Refused(Form(cv: [.. Pdf, .. new byte[1024 * 1024]]), "cv");
        }
        finally
        {
            config["Careers:MaxCvSizeMb"] = null;
        }

        var bot = await ApplyAsync(anonymous, org.Slug, job.Id, Form(email: "bot@example.com", website: "http://spam.example"));
        Assert.Equal(HttpStatusCode.Accepted, bot.StatusCode);
        Assert.Empty(await RecordsAsync(org.Admin, "candidate", "&cf.email=bot@example.com"));
        Assert.Empty(await RecordsAsync(org.Admin, "candidate", "&cf.email=a@example.com"));

        Assert.Equal(HttpStatusCode.NotFound, (await ApplyAsync(anonymous, org.Slug, Guid.NewGuid(), Form())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostJsonAsync($"/api/careers/{org.Slug}/jobs/{job.Id}/apply", new { name = "x" })).StatusCode);

        var platform = await factory.CreateAuthenticatedClientAsync();
        (await platform.PutJsonAsync($"/api/platform/tenants/{org.Id}", new { name = "Talent Co", currency = "AUD", status = "Suspended" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/careers/{org.Slug}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApplyAsync(anonymous, org.Slug, job.Id, Form(email: "late@example.com"))).StatusCode);
    }
}
