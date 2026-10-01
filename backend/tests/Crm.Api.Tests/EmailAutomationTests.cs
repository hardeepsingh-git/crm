using System.Net;
using System.Net.Mail;
using System.Text.Json.Nodes;
using Crm.Api.Automation;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Features.Activities;
using Crm.Api.Features.Customization;
using Crm.Api.Features.Email;
using Crm.Api.Features.Records;
using Crm.Api.Messaging;
using Crm.Api.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crm.Api.Tests;

public class EmailAutomationTests(CrmApiFactory factory) : IClassFixture<CrmApiFactory>
{
    private const string Password = "Ats#Pass12345";

    private sealed record TenantSession(HttpClient Client, Guid Id, string AdminEmail);

    private async Task<TenantSession> RecruitmentTenantAsync()
    {
        var platform = await factory.CreateAuthenticatedClientAsync();
        var slug = $"mail-{Guid.NewGuid():N}"[..20];
        var email = $"admin@{slug}.test";
        var created = await (await platform.PostJsonAsync("/api/platform/tenants", new
        {
            name = "Hire Co", slug, template = "recruitment", currency = "AUD",
            adminEmail = email, adminFirstName = "Rae", adminLastName = "Hirer", adminPassword = Password, seedDemoData = false,
        })).ReadAsync<TenantItem>();
        return new TenantSession(await factory.CreateAuthenticatedClientAsync(email, Password), created.Id, email);
    }

    private static async Task<RecordItem> CreateAsync(HttpClient client, string objectKey, string name, object customFields, string? stage = null)
    {
        var response = await client.PostJsonAsync($"/api/records/{objectKey}", new { name, stage, customFields });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<RecordItem>();
    }

    /// <summary>A job, a candidate and their application.</summary>
    private static async Task<(RecordItem Job, RecordItem Candidate, RecordItem Application)> ApplicantAsync(
        HttpClient ats, string name, string email, string job = "Data Engineer", string? stage = null, object? extra = null)
    {
        var position = await CreateAsync(ats, "position", job, new { status = "Open" });
        var candidate = await CreateAsync(ats, "candidate", name, new { email });
        var fields = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(extra ?? new { }))!.AsObject();
        fields["candidate"] = candidate.Id.ToString();
        fields["position"] = position.Id.ToString();
        return (position, candidate, await CreateAsync(ats, "application", name, fields, stage));
    }

    private async Task<int> DispatchAsync(DateTime? now = null)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().DispatchDueAsync(now ?? DateTime.UtcNow.AddSeconds(1), default);
    }

    private async Task<int> RunScheduleAsync(DateTime now)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ScheduledAutomation>().RunDueAsync(now, default);
    }

    private static async Task<List<OutboundEmailItem>> EmailLogAsync(HttpClient client) =>
        [.. (await (await client.GetAsync("/api/admin/emails?pageSize=100")).ReadAsync<EmailLogPage>()).Page.Items];

    private static async Task<List<ActivityItem>> ActivitiesAsync(HttpClient client, Guid recordId) =>
        [.. (await (await client.GetAsync($"/api/activities?customRecordId={recordId}&pageSize=100")).ReadAsync<PagedResult<ActivityItem>>()).Items];

    private static async Task<RuleItem> RuleAsync(HttpClient client, string name) =>
        (await (await client.GetAsync("/api/admin/automation")).ReadAsync<List<RuleItem>>()).Single(r => r.Name == name);

    [Fact]
    public void Addresses_and_subjects_are_sanitised()
    {
        Assert.Equal("Hello Bcc: victim@example.com", EmailText.SingleLine("Hello\r\nBcc: victim@example.com"));
        Assert.Equal("ann@example.com", EmailText.Address(" Ann@Example.com "));
        Assert.Null(EmailText.Address("ann@example.com, bob@example.com"));
        Assert.Null(EmailText.Address("Ann <ann@example.com>"));
        Assert.Null(EmailText.Address("ann@example.com\r\nBcc: x@example.com"));
        Assert.Null(EmailText.Address("not-an-email"));
    }

    [Fact]
    public async Task Templates_validate_placeholders_and_stay_while_rules_use_them()
    {
        var (ats, _, _) = await RecruitmentTenantAsync();
        var templates = await (await ats.GetAsync("/api/admin/email-templates")).ReadAsync<List<EmailTemplateItem>>();
        Assert.Equal(["Application outcome", "Application received", "Interview invitation", "Job offer", "Offer email", "Offer letter"], templates.Select(t => t.Name).Order());

        var fields = await (await ats.GetAsync("/api/email/fields/application")).ReadAsync<EmailFieldSet>();
        Assert.Contains(fields.Placeholders, p => p.Key == "cf.position.name");
        Assert.Contains(fields.Placeholders, p => p.Key == "cf.candidate.cf.email");
        Assert.Equal(["cf.candidate.cf.email"], fields.Recipients.Select(r => r.Key));

        var typo = await ats.PostJsonAsync("/api/admin/email-templates",
            new { name = "Screening call", objectKey = "application", subject = "Call about {{cf.position.nmae}}", body = "Hi {{name}}" });
        Assert.Equal(HttpStatusCode.BadRequest, typo.StatusCode);
        Assert.Contains("{{cf.position.nmae}}", await typo.Content.ReadAsStringAsync());

        var created = await ats.PostJsonAsync("/api/admin/email-templates",
            new { name = "Screening call", objectKey = "application", subject = "Call about\r\n{{cf.position.name}}", body = "Hi {{name}}" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var screening = await created.ReadAsync<EmailTemplateItem>();
        Assert.Equal("Call about {{cf.position.name}}", screening.Subject);
        Assert.Equal(HttpStatusCode.BadRequest, (await ats.PostJsonAsync("/api/admin/email-templates",
            new { name = "Screening call", objectKey = "candidate", subject = "x", body = "y" })).StatusCode);

        var inUse = await ats.DeleteAsync($"/api/admin/email-templates/{templates.Single(t => t.Name == "Application received").Id}");
        Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        Assert.Contains("Acknowledge new applications", await inUse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await ats.DeleteAsync($"/api/admin/email-templates/{screening.Id}")).StatusCode);

        var standard = await factory.CreateUserClientAsyncIn(ats, "Standard User");
        Assert.Equal(HttpStatusCode.Forbidden, (await standard.GetAsync("/api/admin/email-templates")).StatusCode);
    }

    [Fact]
    public async Task Users_email_a_records_own_addresses_and_it_is_logged_and_delivered()
    {
        var (ats, _, adminEmail) = await RecruitmentTenantAsync();
        var (_, candidate, application) = await ApplicantAsync(ats, "Mia Wong", "Mia.Wong@Example.com");
        var invitation = (await (await ats.GetAsync("/api/email/templates?objectKey=application")).ReadAsync<List<EmailTemplateItem>>())
            .Single(t => t.Name == "Interview invitation");

        var composed = await (await ats.PostJsonAsync("/api/email/compose",
            new { objectKey = "application", recordId = application.Id, templateId = invitation.Id })).ReadAsync<ComposeResult>();
        Assert.Equal(["mia.wong@example.com"], composed.Recipients.Select(r => r.Address));
        Assert.Equal("Interview for Data Engineer", composed.Subject);
        Assert.Contains("Hi Mia Wong,", composed.Body);
        Assert.Contains("Kind regards,\nRae Hirer\nHire Co", composed.Body);
        Assert.Equal(["Next interview"], composed.BlankFields);

        object Send(string to, string subject = "Hello {{name}}", string body = "About {{cf.position.name}}") =>
            new { objectKey = "application", recordId = application.Id, to, subject, body };
        Assert.Equal(HttpStatusCode.BadRequest, (await ats.PostJsonAsync("/api/email/send", Send("someone@elsewhere.test"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ats.PostJsonAsync("/api/email/send", Send("mia.wong@example.com", body: "{{salary}}"))).StatusCode);

        var sent = await ats.PostJsonAsync("/api/email/send", Send("MIA.WONG@example.com"));
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        Assert.Equal(OutboundEmailStatus.Pending, (await sent.ReadAsync<QueuedEmail>()).Status);
        var logged = Assert.Single(await ActivitiesAsync(ats, application.Id), a => a.Type == ActivityType.Email);
        Assert.Equal("Hello Mia Wong", logged.Subject);
        Assert.StartsWith("To: mia.wong@example.com", logged.Description);

        Assert.Empty(factory.Messages.To("mia.wong@example.com"));
        Assert.Equal(1, await DispatchAsync());
        var delivered = Assert.Single(factory.Messages.To("mia.wong@example.com"));
        Assert.Equal(("Hello Mia Wong", "About Data Engineer", adminEmail), (delivered.Subject, delivered.Body, delivered.ReplyTo));
        Assert.Equal(OutboundEmailStatus.Sent, Assert.Single(await EmailLogAsync(ats)).Status);
        Assert.Equal(0, await DispatchAsync());

        var direct = await (await ats.PostJsonAsync("/api/email/compose", new { objectKey = "candidate", recordId = candidate.Id })).ReadAsync<ComposeResult>();
        Assert.Equal(["mia.wong@example.com"], direct.Recipients.Select(r => r.Address));

        var standard = await factory.CreateUserClientAsyncIn(ats, "Standard User");
        Assert.Equal(HttpStatusCode.Forbidden, (await standard.PostJsonAsync("/api/email/compose", new { objectKey = "application", recordId = application.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await standard.PostJsonAsync("/api/email/send", Send("mia.wong@example.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await standard.GetAsync("/api/admin/emails")).StatusCode);
        var other = await RecruitmentTenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostJsonAsync("/api/email/compose", new { objectKey = "application", recordId = application.Id })).StatusCode);
    }

    [Fact]
    public async Task Automation_emails_candidates_once_and_retries_failed_deliveries()
    {
        var (ats, _, _) = await RecruitmentTenantAsync();
        var rule = await RuleAsync(ats, "Email rejected candidates");
        Assert.False(rule.IsActive);
        Assert.Equal(HttpStatusCode.OK, (await ats.PutJsonAsync($"/api/admin/automation/{rule.Id}", rule with { IsActive = true })).StatusCode);

        var (_, _, application) = await ApplicantAsync(ats, "Ann Lee", "ann@example.com", job: "QA Lead");
        async Task MoveAsync(string stage) =>
            (await ats.PatchJsonAsync($"/api/records/application/{application.Id}/stage", new { stage })).EnsureSuccessStatusCode();
        await MoveAsync("rejected");
        await MoveAsync("screening");
        await MoveAsync("rejected");
        var email = Assert.Single(await EmailLogAsync(ats));
        Assert.Equal(("ann@example.com", "Your application for QA Lead", rule.Id), (email.To, email.Subject, email.RuleId));

        factory.Messages.FailWith = m => m.To == "ann@example.com" ? new SmtpException("451 Mailbox busy") : null;
        try
        {
            var now = DateTime.UtcNow.AddSeconds(1);
            Assert.Equal(1, await DispatchAsync(now));
            email = Assert.Single(await EmailLogAsync(ats));
            Assert.Equal((OutboundEmailStatus.Pending, 1, "451 Mailbox busy"), (email.Status, email.Attempts, email.LastError));
            Assert.True(email.NextAttemptAt > now);
            Assert.Equal(0, await DispatchAsync(now));
            for (var attempt = 2; attempt <= EmailDispatcher.MaxAttempts; attempt++)
                Assert.Equal(1, await DispatchAsync(now = now.AddHours(2)));
        }
        finally
        {
            factory.Messages.FailWith = null;
        }
        email = Assert.Single(await EmailLogAsync(ats));
        Assert.Equal((OutboundEmailStatus.Failed, EmailDispatcher.MaxAttempts), (email.Status, email.Attempts));
        Assert.StartsWith(EmailDispatcher.NotSent, (await ActivitiesAsync(ats, application.Id)).Single(a => a.Type == ActivityType.Email).Subject);
        Assert.Empty(factory.Messages.To("ann@example.com"));

        Assert.Equal(HttpStatusCode.OK, (await ats.PostAsync($"/api/admin/emails/{email.Id}/retry", null)).StatusCode);
        Assert.Equal("Your application for QA Lead", (await ActivitiesAsync(ats, application.Id)).Single(a => a.Type == ActivityType.Email).Subject);
        Assert.Equal(1, await DispatchAsync());
        Assert.Contains("we've decided not to progress your application", Assert.Single(factory.Messages.To("ann@example.com")).Body);
        Assert.Equal(HttpStatusCode.BadRequest, (await ats.PostAsync($"/api/admin/emails/{email.Id}/cancel", null)).StatusCode);

        var badRule = new
        {
            name = "Bad", objectKey = "application", trigger = "Created", conditions = Array.Empty<object>(),
            actions = new[] { new { type = "SendEmail", to = "cf.feedback", subject = "Hi", message = "Hello" } },
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await ats.PostJsonAsync("/api/admin/automation", badRule)).StatusCode);
    }

    [Fact]
    public async Task Date_based_rules_fire_once_per_due_date_and_never_for_the_past()
    {
        var (ats, _, _) = await RecruitmentTenantAsync();
        var start = DateTime.UtcNow;
        var (_, _, ana) = await ApplicantAsync(ats, "Ana Ruiz", "ana@example.com", stage: "interview",
            extra: new { interview_date = start.AddHours(30).ToString("O") });
        await ApplicantAsync(ats, "Ben Ode", "ben@example.com", job: "Analyst", stage: "screening",
            extra: new { interview_date = start.AddHours(30).ToString("O") });
        await ApplicantAsync(ats, "Cy Past", "cy@example.com", job: "Tester", stage: "interview",
            extra: new { interview_date = start.AddHours(-2).ToString("O") });

        static async Task<List<string>> RemindersAsync(HttpClient client) =>
            [.. (await (await client.GetAsync("/api/notifications")).ReadAsync<JsonObject>())["items"]!.AsArray()
                .Select(n => n!["message"]!.GetValue<string>()).Where(m => m.StartsWith("Interview with", StringComparison.Ordinal))];

        Assert.Equal(0, await RunScheduleAsync(start.AddHours(1)));
        Assert.Equal(1, await RunScheduleAsync(start.AddHours(7)));
        Assert.Equal(0, await RunScheduleAsync(start.AddHours(7)));
        var reminder = Assert.Single(await RemindersAsync(ats));
        Assert.StartsWith("Interview with Ana Ruiz for Data Engineer at ", reminder);
        Assert.EndsWith(" UTC.", reminder);

        // Rescheduling the interview gives a new due time, which fires once more.
        (await ats.PutJsonAsync($"/api/records/application/{ana.Id}", new
        {
            name = "Ana Ruiz", stage = "interview", customFields = new { interview_date = start.AddHours(50).ToString("O") },
        })).EnsureSuccessStatusCode();
        Assert.Equal(1, await RunScheduleAsync(start.AddHours(27)));
        Assert.Equal(2, (await RemindersAsync(ats)).Count);

        // Standard date columns are filtered in SQL.
        var chase = await ats.PostJsonAsync("/api/admin/automation", new
        {
            name = "Quiet candidates", objectKey = "candidate", trigger = "DateReached", scheduleField = "updatedAt", scheduleOffsetMinutes = 60,
            conditions = Array.Empty<object>(),
            actions = new[] { new { type = "CreateTask", subject = "Check in with {{name}}", dueInDays = 0, assignTo = "owner" } },
        });
        Assert.Equal(HttpStatusCode.Created, chase.StatusCode);
        Assert.Equal(3, await RunScheduleAsync(start.AddHours(2)));
        var tasks = (await (await ats.GetAsync("/api/activities?pageSize=100")).ReadAsync<PagedResult<ActivityItem>>()).Items;
        Assert.Contains(tasks, t => t.Subject == "Check in with Ana Ruiz");

        object Scheduled(string field, string op = "IsNotEmpty") => new
        {
            name = "Invalid", objectKey = "application", trigger = "DateReached", scheduleField = field, scheduleOffsetMinutes = 0,
            conditions = new[] { new { field = "stage", @operator = op, value = (string?)null } },
            actions = new[] { new { type = "Notify", message = "x", assignTo = "owner" } },
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await ats.PostJsonAsync("/api/admin/automation", Scheduled("cf.feedback"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ats.PostJsonAsync("/api/admin/automation", Scheduled("cf.interview_date", "Changed"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ats.PostJsonAsync("/api/admin/automation", Scheduled("cf.interview_date"))).StatusCode);
    }

    [Fact]
    public async Task Daily_limit_caps_sending_and_deleting_a_tenant_removes_its_mail()
    {
        var tenant = await RecruitmentTenantAsync();
        var (_, _, application) = await ApplicantAsync(tenant.Client, "Lim Ito", "lim@example.com");
        object Send() => new { objectKey = "application", recordId = application.Id, to = "lim@example.com", subject = "Hi", body = "Hello" };

        var config = factory.Services.GetRequiredService<IConfiguration>();
        config["Email:DailyLimitPerTenant"] = "1";
        try
        {
            Assert.Equal(HttpStatusCode.Accepted, (await tenant.Client.PostJsonAsync("/api/email/send", Send())).StatusCode);
            var limited = await tenant.Client.PostJsonAsync("/api/email/send", Send());
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Contains("daily limit of 1", await limited.Content.ReadAsStringAsync());
        }
        finally
        {
            config["Email:DailyLimitPerTenant"] = null;
        }

        var platform = await factory.CreateAuthenticatedClientAsync();
        var item = (await (await platform.GetAsync("/api/platform/tenants")).ReadAsync<List<TenantItem>>()).Single(t => t.Id == tenant.Id);
        (await platform.PutJsonAsync($"/api/platform/tenants/{tenant.Id}", new { item.Name, item.Currency, status = "Suspended" })).EnsureSuccessStatusCode();
        Assert.Equal(0, await DispatchAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await platform.DeleteAsync($"/api/platform/tenants/{tenant.Id}")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        Assert.False(await db.OutboundEmails.IgnoreQueryFilters().AnyAsync(e => e.TenantId == tenant.Id));
        Assert.False(await db.EmailTemplates.IgnoreQueryFilters().AnyAsync(e => e.TenantId == tenant.Id));
        Assert.False(await db.ScheduledRuleRuns.IgnoreQueryFilters().AnyAsync(e => e.TenantId == tenant.Id));
    }

    [Fact]
    public async Task Recruitment_tenants_get_email_templates_and_reminders_once_with_candidate_emails_off()
    {
        var (ats, tenantId, _) = await RecruitmentTenantAsync();
        string[] added = ["Acknowledge new applications", "Email rejected candidates", "Interview reminder", "Chase stalled applications"];
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
            await db.EmailTemplates.IgnoreQueryFilters().Where(t => t.TenantId == tenantId).ExecuteDeleteAsync();
            await db.AutomationRules.IgnoreQueryFilters().Where(r => r.TenantId == tenantId && added.Contains(r.Name)).ExecuteDeleteAsync();
            await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.TemplateVersion, 2));
        }

        await UpgradeAsync();
        var rules = await (await ats.GetAsync("/api/admin/automation")).ReadAsync<List<RuleItem>>();
        Assert.All(added, name => Assert.Single(rules, r => r.Name == name));
        Assert.False(rules.Single(r => r.Name == "Acknowledge new applications").IsActive);
        Assert.False(rules.Single(r => r.Name == "Email rejected candidates").IsActive);
        var reminder = rules.Single(r => r.Name == "Interview reminder");
        Assert.True(reminder.IsActive);
        Assert.Equal((AutomationTrigger.DateReached, "cf.interview_date", -1440), (reminder.Trigger, reminder.ScheduleField, reminder.ScheduleOffsetMinutes));
        Assert.Equal(6, (await (await ats.GetAsync("/api/admin/email-templates")).ReadAsync<List<EmailTemplateItem>>()).Count);

        var job = await (await ats.GetAsync("/api/admin/email-templates")).ReadAsync<List<EmailTemplateItem>>();
        Assert.Equal(HttpStatusCode.NoContent, (await ats.DeleteAsync($"/api/admin/email-templates/{job.Single(t => t.Name == "Job offer").Id}")).StatusCode);
        await UpgradeAsync();
        Assert.DoesNotContain(await (await ats.GetAsync("/api/admin/email-templates")).ReadAsync<List<EmailTemplateItem>>(), t => t.Name == "Job offer");
    }

    private async Task UpgradeAsync()
    {
        using var scope = factory.Services.CreateScope();
        await TenantTemplates.UpgradeTenantsAsync(scope.ServiceProvider.GetRequiredService<CrmDbContext>(),
            scope.ServiceProvider.GetRequiredService<TenantContext>(), NullLogger.Instance);
    }
}

public static class TenantUserExtensions
{
    /// <summary>A user with the given role in the tenant <paramref name="admin"/> administers.</summary>
    public static async Task<HttpClient> CreateUserClientAsyncIn(this CrmApiFactory factory, HttpClient admin, params string[] roles) =>
        (await factory.CreateUserAsync(roles, admin: admin)).Client;
}
