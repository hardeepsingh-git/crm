using System.Net;
using System.Text.Json.Nodes;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Features.Activities;
using Crm.Api.Features.Customization;
using Crm.Api.Features.Email;
using Crm.Api.Features.Interviews;
using Crm.Api.Features.Records;
using Crm.Api.Features.Security;
using Crm.Api.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Api.Tests;

public class InterviewTests(CrmApiFactory factory) : IClassFixture<CrmApiFactory>
{
    private const string Password = "Ats#Pass12345";

    private sealed record Org(HttpClient Admin, Guid Id);

    private async Task<Org> RecruitmentTenantAsync()
    {
        var platform = await factory.CreateAuthenticatedClientAsync();
        var slug = $"int-{Guid.NewGuid():N}"[..20];
        var email = $"admin@{slug}.test";
        var created = await (await platform.PostJsonAsync("/api/platform/tenants", new
        {
            name = "Hire Co", slug, template = "recruitment", currency = "AUD",
            adminEmail = email, adminFirstName = "Ivy", adminLastName = "Organiser", adminPassword = Password, seedDemoData = false,
        })).ReadAsync<TenantItem>();
        return new Org(await factory.CreateAuthenticatedClientAsync(email, Password), created.Id);
    }

    private static async Task<RecordItem> CreateAsync(HttpClient client, string objectKey, string name, object customFields, string? stage = null)
    {
        var response = await client.PostJsonAsync($"/api/records/{objectKey}", new { name, stage, customFields });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<RecordItem>();
    }

    private static async Task<(RecordItem Job, RecordItem Application)> ApplicationAsync(
        HttpClient admin, string name, string job = "Platform Engineer", string? source = null, RecordItem? existingJob = null)
    {
        var position = existingJob ?? await CreateAsync(admin, "position", job, new { status = "Open" });
        var candidate = await CreateAsync(admin, "candidate", name, new { email = $"{name.Replace(' ', '.').ToLowerInvariant()}@example.com" });
        var fields = new JsonObject { ["candidate"] = candidate.Id.ToString(), ["position"] = position.Id.ToString() };
        if (source is not null) fields["source"] = source;
        return (position, await CreateAsync(admin, "application", name, fields));
    }

    private static object Schedule(Guid applicationId, DateTime at, Guid[] interviewers, string kind = "Video",
        string? location = "https://meet.example.com/abc", int duration = 60, bool emailCandidate = false) =>
        new { applicationId, scheduledAt = at, durationMinutes = duration, kind, location, notes = "Bring a laptop, please", interviewerIds = interviewers, emailCandidate };

    private static async Task<RecordItem> RecordAsync(HttpClient client, Guid id) =>
        await (await client.GetAsync($"/api/records/application/{id}")).ReadAsync<RecordItem>();

    private static string? NextInterview(RecordItem app) => app.CustomFields["interview_date"]?.GetValue<string>();

    private static async Task<List<string>> NotificationTitlesAsync(HttpClient client) =>
        [.. (await (await client.GetAsync("/api/notifications")).ReadAsync<JsonObject>())["items"]!.AsArray().Select(n => n!["title"]!.GetValue<string>())];

    [Fact]
    public async Task Scheduling_adds_calendar_entries_and_notifications_and_keeps_the_next_interview_date_in_sync()
    {
        var org = await RecruitmentTenantAsync();
        var (a, aId) = await factory.CreateUserAsync(["Standard User"], admin: org.Admin);
        var (b, bId) = await factory.CreateUserAsync(["Standard User"], admin: org.Admin);
        var (job, app) = await ApplicationAsync(org.Admin, "Lena Ortiz");
        var at = DateTime.UtcNow.Date.AddDays(10).AddHours(14);

        var created = await org.Admin.PostJsonAsync("/api/interviews", Schedule(app.Id, at, [aId], emailCandidate: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = await created.ReadAsync<InterviewSaved>();
        Assert.Null(saved.EmailNote);
        Assert.Equal((job.Name, InterviewStatus.Scheduled, true), (saved.Interview.JobTitle, saved.Interview.Status, saved.Interview.CanEdit));
        Assert.Equal(at, DateTime.Parse(NextInterview(await RecordAsync(org.Admin, app.Id))!).ToUniversalTime());
        var meeting = Assert.Single((await (await a.GetAsync($"/api/activities?customRecordId={app.Id}")).ReadAsync<PagedResult<ActivityItem>>()).Items,
            x => x.Type == ActivityType.Meeting);
        Assert.Equal(($"Video interview: Lena Ortiz ({job.Name})", at, aId), (meeting.Subject, meeting.DueAt!.Value.ToUniversalTime(), meeting.OwnerId));
        Assert.Contains("Interview scheduled", await NotificationTitlesAsync(a));
        var invite = Assert.Single((await (await org.Admin.GetAsync("/api/admin/emails")).ReadAsync<EmailLogPage>()).Page.Items);
        Assert.Equal(("lena.ortiz@example.com", $"Interview for {job.Name}"), (invite.To, invite.Subject));

        var later = at.AddDays(2);
        var moved = await org.Admin.PutJsonAsync($"/api/interviews/{saved.Interview.Id}", Schedule(app.Id, later, [bId]));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.DoesNotContain((await (await a.GetAsync($"/api/activities?customRecordId={app.Id}")).ReadAsync<PagedResult<ActivityItem>>()).Items,
            x => x.Type == ActivityType.Meeting && x.OwnerId == aId);
        Assert.Contains("Removed from interview", await NotificationTitlesAsync(a));
        Assert.Single((await (await b.GetAsync($"/api/activities?customRecordId={app.Id}")).ReadAsync<PagedResult<ActivityItem>>()).Items,
            x => x.Type == ActivityType.Meeting && x.OwnerId == bId);

        var sooner = (await (await org.Admin.PostJsonAsync("/api/interviews", Schedule(app.Id, at.AddDays(-5), [bId], kind: "Phone", location: null)))
            .ReadAsync<InterviewSaved>()).Interview;
        Assert.Equal(at.AddDays(-5), DateTime.Parse(NextInterview(await RecordAsync(org.Admin, app.Id))!).ToUniversalTime());
        Assert.Equal(HttpStatusCode.OK, (await org.Admin.PostAsync($"/api/interviews/{sooner.Id}/cancel", null)).StatusCode);
        Assert.Equal(later, DateTime.Parse(NextInterview(await RecordAsync(org.Admin, app.Id))!).ToUniversalTime());
        Assert.Equal(HttpStatusCode.OK, (await org.Admin.PostAsync($"/api/interviews/{saved.Interview.Id}/cancel", null)).StatusCode);
        Assert.Null(NextInterview(await RecordAsync(org.Admin, app.Id)));
        Assert.Equal(HttpStatusCode.BadRequest, (await org.Admin.PostAsync($"/api/interviews/{sooner.Id}/complete", null)).StatusCode);

        var ics = await org.Admin.GetAsync($"/api/interviews/{saved.Interview.Id}/ics");
        Assert.StartsWith("text/calendar", ics.Content.Headers.ContentType!.ToString());
        var text = await ics.Content.ReadAsStringAsync();
        Assert.Contains("BEGIN:VEVENT\r\n", text);
        Assert.Contains($"DTSTART:{later:yyyyMMdd'T'HHmmss'Z'}\r\n", text);
        Assert.Contains("STATUS:CANCELLED", text);
        Assert.Contains("Bring a laptop\\, please", text);
        Assert.All(text.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, line));
    }

    [Fact]
    public async Task Scorecards_stay_blind_until_submitted_and_only_interviewers_give_feedback()
    {
        var org = await RecruitmentTenantAsync();
        var (a, aId) = await factory.CreateUserAsync(["Standard User"], admin: org.Admin);
        var (b, bId) = await factory.CreateUserAsync(["Standard User"], admin: org.Admin);
        var (outsider, _) = await factory.CreateUserAsync(["Standard User"], admin: org.Admin);
        var (_, app) = await ApplicationAsync(org.Admin, "Theo Marsh");
        var held = (await (await org.Admin.PostJsonAsync("/api/interviews", Schedule(app.Id, DateTime.UtcNow.AddHours(-2), [aId, bId])))
            .ReadAsync<InterviewSaved>()).Interview;

        var meta = await (await org.Admin.GetAsync("/api/metadata")).ReadAsync<MetadataDto>();
        Assert.Contains("Communication", meta.Picklists[InterviewEndpoints.CriteriaPicklist]);
        object Card(string rec, int skills = 4) => new { recommendation = rec, ratings = new Dictionary<string, int> { ["Skills"] = skills, ["Communication"] = 5 }, notes = "Solid." };
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PutJsonAsync($"/api/interviews/{held.Id}/scorecard", Card("Yes", skills: 6))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PutJsonAsync($"/api/interviews/{held.Id}/scorecard",
            new { recommendation = "Yes", ratings = new Dictionary<string, int> { ["Charisma"] = 3 } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PutJsonAsync($"/api/interviews/{held.Id}/scorecard", Card("No"))).StatusCode);

        var afterA = await (await a.PutJsonAsync($"/api/interviews/{held.Id}/scorecard", Card("StrongYes"))).ReadAsync<InterviewItem>();
        Assert.Equal(Recommendation.StrongYes, afterA.MyScorecard!.Recommendation);
        Assert.Equal(4, afterA.MyScorecard.Ratings["Skills"]);
        Assert.Contains("Interview feedback", await NotificationTitlesAsync(org.Admin));

        async Task<InterviewItem> AsSeenBy(HttpClient client) =>
            Assert.Single(await (await client.GetAsync($"/api/interviews?applicationId={app.Id}")).ReadAsync<List<InterviewItem>>());
        var bView = await AsSeenBy(b);
        Assert.Empty(bView.Scorecards);
        Assert.Null(bView.MyScorecard);
        Assert.True(bView.Interviewers.Single(x => x.Id == aId).Submitted);
        Assert.Single((await AsSeenBy(org.Admin)).Scorecards);
        Assert.Empty((await AsSeenBy(outsider)).Scorecards);

        var bMine = await (await b.GetAsync("/api/interviews/mine")).ReadAsync<MyInterviews>();
        Assert.Contains(bMine.AwaitingFeedback, i => i.Id == held.Id);
        (await b.PutJsonAsync($"/api/interviews/{held.Id}/scorecard", Card("No", skills: 2))).EnsureSuccessStatusCode();
        Assert.Equal(2, (await AsSeenBy(b)).Scorecards.Count);
        Assert.DoesNotContain((await (await b.GetAsync("/api/interviews/mine")).ReadAsync<MyInterviews>()).AwaitingFeedback, i => i.Id == held.Id);
        Assert.NotNull(Assert.Single((await (await b.GetAsync($"/api/activities?customRecordId={app.Id}")).ReadAsync<PagedResult<ActivityItem>>()).Items,
            x => x.Type == ActivityType.Meeting && x.OwnerId == bId).CompletedAt);

        var upcoming = (await (await org.Admin.PostJsonAsync("/api/interviews", Schedule(app.Id, DateTime.UtcNow.AddDays(3), [aId])))
            .ReadAsync<InterviewSaved>()).Interview;
        Assert.Contains((await (await a.GetAsync("/api/interviews/mine")).ReadAsync<MyInterviews>()).Upcoming, i => i.Id == upcoming.Id);

        Assert.Equal(HttpStatusCode.OK, (await org.Admin.PutJsonAsync($"/api/admin/picklists/{InterviewEndpoints.CriteriaPicklist}",
            new { options = new[] { "Skills", "Leadership" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PutJsonAsync($"/api/interviews/{held.Id}/scorecard", Card("Yes"))).StatusCode);
    }

    [Fact]
    public async Task Scheduling_needs_edit_access_and_valid_details()
    {
        var org = await RecruitmentTenantAsync();
        var (standard, standardId) = await factory.CreateUserAsync(["Standard User"], admin: org.Admin);
        var (_, app) = await ApplicationAsync(org.Admin, "Ravi Iyer");
        var soon = DateTime.UtcNow.AddDays(2);

        Assert.Equal(HttpStatusCode.Forbidden, (await standard.PostJsonAsync("/api/interviews", Schedule(app.Id, soon, [standardId]))).StatusCode);
        var other = await RecruitmentTenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PostJsonAsync("/api/interviews", Schedule(app.Id, soon, [standardId]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/interviews?applicationId={app.Id}")).StatusCode);

        async Task Refused(object body, string field)
        {
            var response = await org.Admin.PostJsonAsync("/api/interviews", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync());
        }
        await Refused(Schedule(app.Id, soon, []), "interviewerIds");
        await Refused(Schedule(app.Id, soon, [Guid.NewGuid()]), "interviewerIds");
        var outsiderId = (await factory.CreateUserAsync(["Standard User"], admin: other.Admin)).UserId;
        await Refused(Schedule(app.Id, soon, [outsiderId]), "interviewerIds");
        await Refused(Schedule(app.Id, soon, [standardId], duration: 5), "durationMinutes");
        await Refused(Schedule(app.Id, soon, [standardId], location: "Room 4"), "location");
        await Refused(Schedule(app.Id, DateTime.UtcNow.AddYears(2), [standardId]), "scheduledAt");
        Assert.Equal(HttpStatusCode.Created, (await org.Admin.PostJsonAsync("/api/interviews",
            Schedule(app.Id, soon, [standardId], kind: "Onsite", location: "Room 4"))).StatusCode);
    }

    [Fact]
    public async Task Hiring_analytics_follow_the_funnel_sources_and_permissions()
    {
        var org = await RecruitmentTenantAsync();
        var (job, applied) = await ApplicationAsync(org.Admin, "Ann Applied", source: "LinkedIn");
        var (_, hired) = await ApplicationAsync(org.Admin, "Hal Hired", source: "Referral", existingJob: job);
        var (_, rejected) = await ApplicationAsync(org.Admin, "Rae Rejected", source: "LinkedIn", existingJob: job);
        var (_, screening) = await ApplicationAsync(org.Admin, "Sol Screening", source: "Website", existingJob: job);
        async Task MoveAsync(RecordItem app, params string[] stages)
        {
            foreach (var stage in stages)
                (await org.Admin.PatchJsonAsync($"/api/records/application/{app.Id}/stage", new { stage })).EnsureSuccessStatusCode();
        }
        await MoveAsync(hired, "screening", "interview", "offer", "hired");
        await MoveAsync(rejected, "screening", "rejected");
        await MoveAsync(screening, "screening");
        var me = await (await org.Admin.GetAsync("/api/auth/me")).ReadAsync<JsonObject>();
        (await org.Admin.PostJsonAsync("/api/interviews", Schedule(screening.Id, DateTime.UtcNow.AddDays(2), [Guid.Parse(me["id"]!.GetValue<string>())])))
            .EnsureSuccessStatusCode();

        var report = await (await org.Admin.GetAsync("/api/recruiting/analytics?days=30")).ReadAsync<HiringAnalytics>();
        Assert.Equal((4, 2, 1, 1), (report.Applications, report.Open, report.Hired, report.Rejected));
        Assert.NotNull(report.AverageDaysToHire);
        FunnelStage Stage(string key) => report.Funnel.Single(s => s.Key == key);
        Assert.Equal((4, 3, 1, 1), (Stage("applied").Reached, Stage("screening").Reached, Stage("interview").Reached, Stage("offer").Reached));
        Assert.Equal(0.75, Stage("applied").ConversionToNext);
        Assert.Equal(1.0, Stage("offer").ConversionToNext);
        Assert.Equal((1, 1), (Stage("applied").Current, Stage("screening").Current));
        Assert.NotNull(Stage("screening").AverageDaysInStage);
        var linkedIn = report.Sources.Single(s => s.Source == "LinkedIn");
        Assert.Equal((2, 0), (linkedIn.Applications, linkedIn.Hired));
        Assert.Equal(1.0, report.Sources.Single(s => s.Source == "Referral").HireRate);
        var row = Assert.Single(report.Jobs);
        Assert.Equal((job.Name, 4, 2, 1), (row.Title, row.Applications, row.Open, row.Hired));
        Assert.Equal(1, report.InterviewsNext7Days);

        var otherJob = await CreateAsync(org.Admin, "position", "Other role", new { status = "Open" });
        Assert.Equal(0, (await (await org.Admin.GetAsync($"/api/recruiting/analytics?positionId={otherJob.Id}")).ReadAsync<HiringAnalytics>()).Applications);
        var ownRole = await (await org.Admin.PostJsonAsync("/api/admin/roles", new
        {
            name = "Own only", description = (string?)null, permissions = new[] { new { resource = "*", action = "read", scope = "Own" } },
        })).ReadAsync<RoleItem>();
        var limited = await factory.CreateUserClientAsyncIn(org.Admin, ownRole.Name);
        Assert.Equal(0, (await (await limited.GetAsync("/api/recruiting/analytics")).ReadAsync<HiringAnalytics>()).Applications);

        var platform = await factory.CreateAuthenticatedClientAsync();
        (await platform.PutJsonAsync($"/api/platform/tenants/{org.Id}", new { name = "Hire Co", currency = "AUD", status = "Suspended" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await platform.DeleteAsync($"/api/platform/tenants/{org.Id}")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        Assert.False(await db.Interviews.IgnoreQueryFilters().AnyAsync(i => i.TenantId == org.Id));
    }
}
