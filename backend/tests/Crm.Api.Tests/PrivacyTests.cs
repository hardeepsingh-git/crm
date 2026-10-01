using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Features.Offers;
using Crm.Api.Features.Privacy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Api.Tests;

public class PrivacyTests(CrmApiFactory factory) : IClassFixture<CrmApiFactory>
{
    private sealed record Hire(Guid CandidateId, Guid ApplicationId, Guid InterviewId, Guid OfferId, string Name);

    /// <summary>A candidate with a CV, an application, an interview with a scorecard, a sent offer and an email.</summary>
    private static async Task<Hire> CandidateWithHistoryAsync(Ats.Org org, string name)
    {
        var (_, candidate, app) = await Ats.ApplicationAsync(org.Admin, name);
        using (var form = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent("%PDF-1.4 cv"u8.ToArray());
            file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(file, "file", "cv.pdf");
            (await org.Admin.PostAsync($"/api/attachments?objectKey=candidate&recordId={candidate.Id}", form)).EnsureSuccessStatusCode();
        }
        var interview = (await (await org.Admin.PostJsonAsync("/api/interviews", new
        {
            applicationId = app.Id, scheduledAt = DateTime.UtcNow.AddHours(-3), durationMinutes = 45, kind = "Phone",
            interviewerIds = new[] { org.AdminId }, emailCandidate = false,
        })).ReadAsync<JsonObject>())["interview"]!["id"]!.GetValue<string>();
        (await org.Admin.PutJsonAsync($"/api/interviews/{interview}/scorecard", new { recommendation = "Yes", notes = "Strong communicator" }))
            .EnsureSuccessStatusCode();
        var offer = await (await org.Admin.PostJsonAsync("/api/offers", Ats.Terms(app.Id))).ReadAsync<OfferItem>();
        (await org.Admin.PostAsync($"/api/offers/{offer.Id}/send", null)).EnsureSuccessStatusCode();
        return new Hire(candidate.Id, app.Id, Guid.Parse(interview), offer.Id, name);
    }

    [Fact]
    public async Task Candidate_data_export_contains_their_records_and_files_without_naming_interviewers()
    {
        var org = await Ats.TenantAsync(factory);
        var hire = await CandidateWithHistoryAsync(org, "Lena Ortiz");
        var (standard, _) = await factory.CreateUserAsync(["Standard User"], admin: org.Admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await standard.GetAsync($"/api/privacy/candidates/{hire.CandidateId}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await org.Admin.GetAsync($"/api/privacy/candidates/{hire.ApplicationId}/export")).StatusCode);

        var response = await org.Admin.GetAsync($"/api/privacy/candidates/{hire.CandidateId}/export");
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var json = await new StreamReader(zip.GetEntry("data.json")!.Open()).ReadToEndAsync();
        var data = JsonNode.Parse(json)!;
        Assert.Equal("Lena Ortiz", data["candidate"]!["name"]!.GetValue<string>());
        Assert.Equal("lena.ortiz@example.com", data["candidate"]!["fields"]!["Email"]!.GetValue<string>());
        var application = data["applications"]![0]!;
        Assert.Equal("Offer", application["stage"]!.GetValue<string>());
        var card = application["interviews"]![0]!["scorecards"]![0]!;
        Assert.Equal(("Interviewer 1", "Strong communicator"), (card["interviewer"]!.GetValue<string>(), card["notes"]!.GetValue<string>()));
        Assert.DoesNotContain("Olive", card.ToJsonString());
        Assert.Equal(120000m, application["offers"]![0]!["salary"]!.GetValue<decimal>());
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("files/", StringComparison.Ordinal) && e.FullName.EndsWith("cv.pdf", StringComparison.Ordinal));

        var audit = await (await org.Admin.GetAsync("/api/admin/audit?action=Exported")).Content.ReadAsStringAsync();
        Assert.Contains("Personal data export", audit);
    }

    [Fact]
    public async Task Erasing_a_candidate_removes_their_data_and_anonymises_the_audit_trail()
    {
        var org = await Ats.TenantAsync(factory);
        var hire = await CandidateWithHistoryAsync(org, "Omar Khan");
        var (_, other, otherApp) = await Ats.ApplicationAsync(org.Admin, "Keep Me", job: "Analyst");

        var refused = await org.Admin.PostJsonAsync($"/api/privacy/candidates/{hire.CandidateId}/erase", new { confirmName = "Omar" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var erased = await (await org.Admin.PostJsonAsync($"/api/privacy/candidates/{hire.CandidateId}/erase", new { confirmName = " omar KHAN" }))
            .ReadAsync<ErasureResult>();
        Assert.Equal(1, erased.Applications);
        Assert.Equal(1, erased.Files);
        Assert.Equal(1, erased.Emails);
        Assert.True(erased.Activities >= 2);

        Assert.Equal(HttpStatusCode.NotFound, (await org.Admin.GetAsync($"/api/records/candidate/{hire.CandidateId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await org.Admin.GetAsync($"/api/records/application/{hire.ApplicationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await org.Admin.GetAsync($"/api/records/candidate/{other.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await org.Admin.GetAsync($"/api/records/application/{otherApp.Id}")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        Assert.False(await db.Interviews.IgnoreQueryFilters().AnyAsync(i => i.Id == hire.InterviewId));
        Assert.False(await db.Scorecards.IgnoreQueryFilters().AnyAsync(s => s.InterviewId == hire.InterviewId));
        Assert.False(await db.Offers.IgnoreQueryFilters().AnyAsync(o => o.Id == hire.OfferId));
        Assert.False(await db.Attachments.IgnoreQueryFilters().AnyAsync(a => a.RecordId == hire.CandidateId));
        Assert.False(await db.OutboundEmails.IgnoreQueryFilters().AnyAsync(e => e.To == "omar.khan@example.com"));
        Assert.False(await db.Activities.IgnoreQueryFilters().AnyAsync(a => a.CustomRecordId == hire.ApplicationId));
        var audit = await db.AuditLogs.IgnoreQueryFilters().Where(a => a.TenantId == org.Id).ToListAsync();
        Assert.DoesNotContain(audit, a => (a.EntityName ?? "").Contains("Omar", StringComparison.OrdinalIgnoreCase)
                                          || a.Changes.Contains("Omar", StringComparison.OrdinalIgnoreCase)
                                          || a.Changes.Contains("omar.khan", StringComparison.OrdinalIgnoreCase));
        var entry = Assert.Single(audit, a => a.Action == AuditAction.Erased);
        Assert.Equal((hire.CandidateId, PrivacyEndpoints.ErasedName), (entry.EntityId, entry.EntityName));
        Assert.Contains("\"reason\":\"Request\"", entry.Changes);
        Assert.Contains(audit, a => a.EntityName == "Keep Me");
    }

    [Fact]
    public async Task Retention_policy_previews_then_erases_only_inactive_closed_candidates()
    {
        var org = await Ats.TenantAsync(factory);
        async Task<(Guid Candidate, Guid Application)> AppAt(string name, string? stage)
        {
            var (_, candidate, app) = await Ats.ApplicationAsync(org.Admin, name, job: $"Job for {name}");
            if (stage is not null) (await org.Admin.PatchJsonAsync($"/api/records/application/{app.Id}/stage", new { stage })).EnsureSuccessStatusCode();
            return (candidate.Id, app.Id);
        }
        var rejected = await AppAt("Old Rejected", "rejected");
        var hired = await AppAt("Old Hired", "hired");
        var open = await AppAt("Old Open", null);
        var recent = await AppAt("Recent Rejected", "rejected");
        var sourced = await Ats.CreateAsync(org.Admin, "candidate", "Old Sourced", new { email = "old.sourced@example.com" });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
            var old = DateTime.UtcNow.AddMonths(-30);
            Guid[] aged = [rejected.Candidate, rejected.Application, hired.Candidate, hired.Application, open.Candidate, open.Application, sourced.Id];
            await db.CustomRecords.IgnoreQueryFilters().Where(r => aged.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.UpdatedAt, old));
        }

        var off = await (await org.Admin.GetAsync("/api/privacy/retention")).ReadAsync<RetentionStatus>();
        Assert.Equal((null, 0), (off.Months, off.Eligible));
        Assert.Equal(HttpStatusCode.BadRequest, (await org.Admin.PutJsonAsync("/api/privacy/retention", new { months = 3 })).StatusCode);
        var (standard, _) = await factory.CreateUserAsync(["Standard User"], admin: org.Admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await standard.PutJsonAsync("/api/privacy/retention", new { months = 24 })).StatusCode);

        var preview = await (await org.Admin.PutJsonAsync("/api/privacy/retention", new { months = 24 })).ReadAsync<RetentionStatus>();
        Assert.Equal(24, preview.Months);
        Assert.Equal(["Old Rejected", "Old Sourced"], preview.Sample.Select(s => s.Name).Order().ToArray());

        var run = await (await org.Admin.PostAsync("/api/privacy/retention/run", null)).ReadAsync<RetentionRun>();
        Assert.Equal((2, 0), (run.Erased, run.Remaining));
        foreach (var gone in new[] { rejected.Candidate, sourced.Id })
            Assert.Equal(HttpStatusCode.NotFound, (await org.Admin.GetAsync($"/api/records/candidate/{gone}")).StatusCode);
        foreach (var kept in new[] { hired.Candidate, open.Candidate, recent.Candidate })
            Assert.Equal(HttpStatusCode.OK, (await org.Admin.GetAsync($"/api/records/candidate/{kept}")).StatusCode);
        Assert.Equal(0, (await (await org.Admin.GetAsync("/api/privacy/retention")).ReadAsync<RetentionStatus>()).Eligible);
    }
}
