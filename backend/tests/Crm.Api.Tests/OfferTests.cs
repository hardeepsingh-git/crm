using System.Net;
using System.Text.Json.Nodes;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Features.Activities;
using Crm.Api.Features.Email;
using Crm.Api.Features.Interviews;
using Crm.Api.Features.Offers;
using Crm.Api.Features.Records;
using Crm.Api.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Api.Tests;

/// <summary>A recruitment tenant without demo data, and helpers to add jobs, candidates and applications to it.</summary>
internal static class Ats
{
    public const string Password = "Ats#Pass12345";

    public sealed record Org(HttpClient Admin, Guid Id, Guid AdminId);

    public static async Task<Org> TenantAsync(CrmApiFactory factory)
    {
        var platform = await factory.CreateAuthenticatedClientAsync();
        var slug = $"ats-{Guid.NewGuid():N}"[..20];
        var email = $"admin@{slug}.test";
        var created = await (await platform.PostJsonAsync("/api/platform/tenants", new
        {
            name = "Offer Co", slug, template = "recruitment", currency = "AUD",
            adminEmail = email, adminFirstName = "Olive", adminLastName = "Owner", adminPassword = Password, seedDemoData = false,
        })).ReadAsync<TenantItem>();
        var admin = await factory.CreateAuthenticatedClientAsync(email, Password);
        var me = await (await admin.GetAsync("/api/auth/me")).ReadAsync<JsonObject>();
        return new Org(admin, created.Id, Guid.Parse(me["id"]!.GetValue<string>()));
    }

    public static async Task<RecordItem> CreateAsync(HttpClient client, string objectKey, string name, object customFields)
    {
        var response = await client.PostJsonAsync($"/api/records/{objectKey}", new { name, customFields });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<RecordItem>();
    }

    public static async Task<(RecordItem Job, RecordItem Candidate, RecordItem Application)> ApplicationAsync(
        HttpClient admin, string name, string job = "Platform Engineer", decimal? expectedSalary = null)
    {
        var position = await CreateAsync(admin, "position", job, new { status = "Open" });
        var candidate = await CreateAsync(admin, "candidate", name, new
        {
            email = $"{name.Replace(' ', '.').ToLowerInvariant()}@example.com", expected_salary = expectedSalary,
        });
        var app = await CreateAsync(admin, "application", name, new { candidate = candidate.Id.ToString(), position = position.Id.ToString() });
        return (position, candidate, app);
    }

    public static object Terms(Guid applicationId, decimal salary = 120000m, string currency = "AUD", int startInDays = 30,
        double expiresInHours = 72, string? letter = "Dear {{name}},\n\nWe offer you {{offer.job_title}} at {{offer.salary}}, starting {{offer.start_date}}.",
        string? jobTitle = "Platform Engineer") =>
        new
        {
            applicationId, jobTitle, salary, currency, payPeriod = "Year",
            startDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(startInDays).ToString("yyyy-MM-dd"),
            expiresAt = DateTime.UtcNow.AddHours(expiresInHours), letter, notes = "Approved by finance",
        };

    public static string TokenOf(string link) => link[(link.LastIndexOf('/') + 1)..];
}

public class OfferTests(CrmApiFactory factory) : IClassFixture<CrmApiFactory>
{
    private static async Task<List<string>> NotificationTitlesAsync(HttpClient client) =>
        [.. (await (await client.GetAsync("/api/notifications")).ReadAsync<JsonObject>())["items"]!.AsArray().Select(n => n!["title"]!.GetValue<string>())];

    private async Task<OfferSent> SendAsync(HttpClient admin, Guid offerId)
    {
        var response = await admin.PostAsync($"/api/offers/{offerId}/send", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<OfferSent>();
    }

    [Fact]
    public async Task Offers_are_drafted_sent_and_accepted_with_signature_evidence()
    {
        var org = await Ats.TenantAsync(factory);
        var (job, _, app) = await Ats.ApplicationAsync(org.Admin, "Nina Patel", expectedSalary: 118000m);

        var defaults = await (await org.Admin.GetAsync($"/api/offers/defaults?applicationId={app.Id}")).ReadAsync<OfferDefaults>();
        Assert.Equal((job.Name, 118000m, "AUD"), (defaults.JobTitle, defaults.Salary, defaults.Currency));
        Assert.Contains("{{offer.salary}}", defaults.Letter);

        var created = await org.Admin.PostJsonAsync("/api/offers", Ats.Terms(app.Id, letter: defaults.Letter));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = await created.ReadAsync<OfferItem>();
        Assert.Equal(OfferStatus.Draft, draft.Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await org.Admin.PostJsonAsync("/api/offers", Ats.Terms(app.Id))).StatusCode);

        (await org.Admin.PutJsonAsync($"/api/offers/{draft.Id}", Ats.Terms(app.Id, salary: 125000m, letter: defaults.Letter))).EnsureSuccessStatusCode();
        var preview = await (await org.Admin.GetAsync($"/api/offers/{draft.Id}/preview")).ReadAsync<OfferPreview>();
        Assert.Contains("Dear Nina Patel", preview.Letter);
        Assert.Contains("AUD 125,000.00 per year", preview.Letter);
        Assert.Empty(preview.BlankFields);

        var sent = await SendAsync(org.Admin, draft.Id);
        Assert.StartsWith("https://crm.test/offer/", sent.Link);
        Assert.Null(sent.EmailNote);
        Assert.Equal(OfferStatus.Sent, sent.Offer.Status);
        Assert.DoesNotContain("{{", sent.Offer.Letter);
        Assert.Equal("offer", (await (await org.Admin.GetAsync($"/api/records/application/{app.Id}")).ReadAsync<RecordItem>()).Stage);

        var token = Ats.TokenOf(sent.Link);
        var email = Assert.Single((await (await org.Admin.GetAsync("/api/admin/emails")).ReadAsync<EmailLogPage>()).Page.Items);
        Assert.Equal("nina.patel@example.com", email.To);
        var timeline = (await (await org.Admin.GetAsync($"/api/activities?customRecordId={app.Id}&type=Email")).ReadAsync<PagedResult<ActivityItem>>()).Items;
        Assert.DoesNotContain(token, Assert.Single(timeline).Description);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
            Assert.Contains(token, (await db.OutboundEmails.IgnoreQueryFilters().SingleAsync(e => e.Id == email.Id)).Body);
            Assert.DoesNotContain(await db.AuditLogs.IgnoreQueryFilters().Where(a => a.EntityId == draft.Id).Select(a => a.Changes).ToListAsync(),
                c => c.Contains("tokenHash", StringComparison.OrdinalIgnoreCase));
        }

        var candidate = factory.CreateClient();
        var view = await (await candidate.GetAsync($"/api/offer-response/{token}")).ReadAsync<PublicOffer>();
        Assert.Equal(("Offer Co", "Nina Patel", OfferStatus.Sent, 125000m), (view.Organisation, view.CandidateName, view.Status, view.Salary));
        Assert.Contains("Dear Nina Patel", view.Letter);

        var wrong = await candidate.PostJsonAsync($"/api/offer-response/{token}/accept", new { signedName = "Someone Else", agree = true });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("Nina Patel", await wrong.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await candidate.PostJsonAsync($"/api/offer-response/{token}/accept", new { signedName = "Nina Patel", agree = false })).StatusCode);
        var accepted = await candidate.PostJsonAsync($"/api/offer-response/{token}/accept", new { signedName = "  nina   PATEL ", agree = true });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(OfferStatus.Accepted, (await accepted.ReadAsync<OfferAnswered>()).Status);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await candidate.PostJsonAsync($"/api/offer-response/{token}/decline", new { reason = "Changed my mind" })).StatusCode);

        var after = await (await candidate.GetAsync($"/api/offer-response/{token}")).ReadAsync<PublicOffer>();
        Assert.Equal((OfferStatus.Accepted, "nina   PATEL"), (after.Status, after.SignedName));
        var item = Assert.Single(await (await org.Admin.GetAsync($"/api/offers?applicationId={app.Id}")).ReadAsync<List<OfferItem>>());
        Assert.Equal(OfferStatus.Accepted, item.Status);
        Assert.NotNull(item.RespondedAt);
        var notes = (await (await org.Admin.GetAsync($"/api/activities?customRecordId={app.Id}&type=Note")).ReadAsync<PagedResult<ActivityItem>>()).Items;
        Assert.Contains(notes, n => n.Subject == "Offer accepted" && n.Description!.Contains("nina   PATEL"));
    }

    [Fact]
    public async Task Offers_can_be_resent_declined_withdrawn_or_expire_and_count_in_analytics()
    {
        var org = await Ats.TenantAsync(factory);
        var (_, _, app) = await Ats.ApplicationAsync(org.Admin, "Theo Marsh");
        var candidate = factory.CreateClient();

        var first = await (await org.Admin.PostJsonAsync("/api/offers", Ats.Terms(app.Id))).ReadAsync<OfferItem>();
        var oldToken = Ats.TokenOf((await SendAsync(org.Admin, first.Id)).Link);
        var newToken = Ats.TokenOf((await SendAsync(org.Admin, first.Id)).Link);
        Assert.Equal(HttpStatusCode.NotFound, (await candidate.GetAsync($"/api/offer-response/{oldToken}")).StatusCode);
        (await candidate.PostJsonAsync($"/api/offer-response/{newToken}/decline", new { reason = "Accepted another role" })).EnsureSuccessStatusCode();
        Assert.Contains("Offer declined", await (await org.Admin.GetAsync("/api/notifications")).Content.ReadAsStringAsync());

        var second = await (await org.Admin.PostJsonAsync("/api/offers", Ats.Terms(app.Id, salary: 130000m))).ReadAsync<OfferItem>();
        var secondToken = Ats.TokenOf((await SendAsync(org.Admin, second.Id)).Link);
        var withdrawn = await org.Admin.PostJsonAsync($"/api/offers/{second.Id}/withdraw", new { reason = "Role put on hold" });
        Assert.Equal(OfferStatus.Withdrawn, (await withdrawn.ReadAsync<OfferItem>()).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await candidate.GetAsync($"/api/offer-response/{secondToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await org.Admin.PostAsync($"/api/offers/{second.Id}/send", null)).StatusCode);

        var third = await (await org.Admin.PostJsonAsync("/api/offers", Ats.Terms(app.Id, expiresInHours: 3))).ReadAsync<OfferItem>();
        var thirdToken = Ats.TokenOf((await SendAsync(org.Admin, third.Id)).Link);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
            await db.Offers.IgnoreQueryFilters().Where(o => o.Id == third.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }
        var expired = await (await candidate.GetAsync($"/api/offer-response/{thirdToken}")).ReadAsync<PublicOffer>();
        Assert.Equal(OfferStatus.Expired, expired.Status);
        Assert.Null(expired.Salary);
        Assert.Null(expired.Letter);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await candidate.PostJsonAsync($"/api/offer-response/{thirdToken}/accept", new { signedName = "Theo Marsh", agree = true })).StatusCode);

        var drafts = await (await org.Admin.PostJsonAsync("/api/offers", Ats.Terms(app.Id))).ReadAsync<OfferItem>();
        Assert.Equal(HttpStatusCode.NoContent, (await org.Admin.DeleteAsync($"/api/offers/{drafts.Id}")).StatusCode);

        var stats = (await (await org.Admin.GetAsync("/api/recruiting/analytics?days=30")).ReadAsync<HiringAnalytics>()).Offers;
        Assert.Equal((3, 0, 1, 1, 0), (stats.Sent, stats.Accepted, stats.Declined, stats.Expired, stats.Pending));
        Assert.Equal(0, stats.AcceptanceRate);
    }

    [Fact]
    public async Task Offers_need_edit_access_valid_terms_and_a_real_link()
    {
        var org = await Ats.TenantAsync(factory);
        var (_, _, app) = await Ats.ApplicationAsync(org.Admin, "Ravi Iyer");
        var (standard, _) = await factory.CreateUserAsync(["Standard User"], admin: org.Admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await standard.GetAsync($"/api/offers?applicationId={app.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await standard.PostJsonAsync("/api/offers", Ats.Terms(app.Id))).StatusCode);
        var other = await Ats.TenantAsync(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/offers?applicationId={app.Id}")).StatusCode);

        async Task Refused(object body, string field)
        {
            var response = await org.Admin.PostJsonAsync("/api/offers", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync());
        }
        await Refused(Ats.Terms(app.Id, salary: 0), "salary");
        await Refused(Ats.Terms(app.Id, currency: "aud"), "currency");
        await Refused(Ats.Terms(app.Id, startInDays: -1), "startDate");
        await Refused(Ats.Terms(app.Id, expiresInHours: 24 * 61), "expiresAt");
        await Refused(Ats.Terms(app.Id, letter: "Hi {{oops}}"), "letter");
        await Refused(Ats.Terms(app.Id, jobTitle: " "), "jobTitle");

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/offer-response/{new string('x', 43)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/offer-response/short")).StatusCode);
    }
}
