using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Crm.Api.Automation;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Messaging;
using Crm.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Offers;

public record OfferRequest(
    Guid ApplicationId,
    [MaxLength(200)] string? JobTitle,
    decimal Salary,
    [MaxLength(3)] string Currency,
    PayPeriod PayPeriod,
    DateOnly StartDate,
    DateTime ExpiresAt,
    [MaxLength(20000)] string? Letter,
    [MaxLength(2000)] string? Notes);

public record OfferItem(
    Guid Id, Guid ApplicationId, OfferStatus Status, string JobTitle, decimal Salary, string Currency, PayPeriod PayPeriod,
    DateOnly StartDate, DateTime ExpiresAt, string Letter, string? Notes, string CreatedByName, DateTime CreatedAt,
    DateTime? SentAt, string? SentByName, DateTime? RespondedAt, string? SignedName, string? ResponseIp, string? DeclineReason);

/// <summary>Suggested values for a new offer: the job's title, the candidate's expected salary and the organisation's letter.</summary>
public record OfferDefaults(string JobTitle, decimal? Salary, string Currency, PayPeriod PayPeriod, DateOnly StartDate, DateTime ExpiresAt, string Letter);

public record OfferPreview(string Letter, IReadOnlyList<string> BlankFields);

/// <param name="Link">The candidate's link; shown once, so it can be shared another way if the email can't be sent.</param>
public record OfferSent(OfferItem Offer, string Link, string? EmailNote);

public record WithdrawOfferRequest([MaxLength(2000)] string? Reason);

/// <summary>Offer template placeholders and their values; used by letters, offer emails and other application emails.</summary>
public static class OfferKeys
{
    public const string ApplicationKey = "application";
    public const string LetterTemplate = "Offer letter";
    public const string EmailTemplate = "Offer email";
    public const string LinkKey = "offer.link";

    public static readonly EmailField[] Placeholders =
    [
        new("offer.job_title", "Offer → Job title"),
        new("offer.salary", "Offer → Salary"),
        new("offer.start_date", "Offer → Start date"),
        new("offer.expires", "Offer → Respond by"),
        new(LinkKey, "Offer → Link to respond (offer email only)"),
    ];

    public static IEnumerable<KeyValuePair<string, object?>> Values(Offer? offer)
    {
        var inv = CultureInfo.InvariantCulture;
        yield return new("offer.job_title", offer?.JobTitle ?? "");
        yield return new("offer.salary", offer is null ? "" : Money(offer));
        yield return new("offer.start_date", offer?.StartDate.ToString("d MMMM yyyy", inv) ?? "");
        yield return new("offer.expires", offer is null ? "" : offer.ExpiresAt.ToString("d MMMM yyyy HH:mm", inv) + " UTC");
        yield return new(LinkKey, "");
    }

    public static string Money(Offer o) =>
        $"{o.Currency} {o.Salary.ToString("N2", CultureInfo.InvariantCulture)} per {o.PayPeriod.ToString().ToLowerInvariant()}";

    public const string DefaultLetter =
        "Dear {{name}},\n\nWe are delighted to offer you the position of {{offer.job_title}} at {{organisation.name}}.\n\n" +
        "Salary: {{offer.salary}}\nStart date: {{offer.start_date}}\n\n" +
        "This offer is subject to your employment contract, which we will send separately. " +
        "Please let us know your decision by {{offer.expires}}.\n\n" +
        "We look forward to welcoming you to the team.\n\nYours sincerely,\n{{owner.name}}\n{{organisation.name}}";

    public const string DefaultEmailSubject = "Your offer from {{organisation.name}}";

    public const string DefaultEmailBody =
        "Hi {{name}},\n\nCongratulations! Your offer for the {{offer.job_title}} role is ready. " +
        "You can read it and accept or decline online:\n\n{{offer.link}}\n\nThe offer is open until {{offer.expires}}." +
        "\n\nKind regards,\n{{owner.name}}\n{{organisation.name}}";
}

/// <summary>
/// Job offers on applications. Offers carry pay details, so every endpoint needs update access to the application (the
/// hiring team). Sending creates a single-purpose link for the candidate; a new send replaces the old link.
/// </summary>
public static class OfferEndpoints
{
    private const string ApplicationKey = OfferKeys.ApplicationKey;
    public const int MaxValidityDays = 60;
    public const decimal MaxSalary = 100_000_000m;

    public static RouteGroupBuilder MapOffers(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/offers").WithTags("Offers");
        g.MapGet("/", List);
        g.MapGet("/defaults", Defaults);
        g.MapPost("/", Create);
        g.MapPut("/{id:guid}", Update);
        g.MapDelete("/{id:guid}", Delete);
        g.MapGet("/{id:guid}/preview", Preview);
        g.MapPost("/{id:guid}/send", Send);
        g.MapPost("/{id:guid}/withdraw", Withdraw);
        return api;
    }

    // ---------- Shared ----------

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Sent offers past their deadline become Expired; checked whenever offers are read or answered.</summary>
    public static bool ExpireIfDue(Offer offer, DateTime now)
    {
        if (offer.Status != OfferStatus.Sent || offer.ExpiresAt > now) return false;
        offer.Status = OfferStatus.Expired;
        return true;
    }

    public static bool IsActive(OfferStatus status) => status is OfferStatus.Draft or OfferStatus.Sent;

    /// <summary>The application, if the caller may read it; and whether they're on its hiring team (may update it).</summary>
    private static async Task<(CustomRecord? App, IResult? Denied)> ApplicationAsync(CrmDbContext db, EffectivePermissions p, Guid id, CancellationToken ct)
    {
        var app = await db.CustomRecords.FirstOrDefaultAsync(r => r.Id == id && r.ObjectDefinition!.Key == ApplicationKey, ct);
        if (app is null || !p.CanAccess(ApplicationKey, PermissionActions.Read, app.OwnerId)) return (null, TypedResults.NotFound());
        if (!p.CanAccess(ApplicationKey, PermissionActions.Update, app.OwnerId)) return (null, TypedResults.Forbid());
        return (app, null);
    }

    private static async Task<(Offer? Offer, CustomRecord? App, IResult? Denied)> OfferAsync(CrmDbContext db, EffectivePermissions p, Guid id, CancellationToken ct)
    {
        var offer = await db.Offers.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (offer is null) return (null, null, TypedResults.NotFound());
        var (app, denied) = await ApplicationAsync(db, p, offer.ApplicationId, ct);
        return (offer, app, denied);
    }

    private static async Task<List<OfferItem>> ToItemsAsync(CrmDbContext db, List<Offer> offers, Guid tenantId, CancellationToken ct)
    {
        var userIds = offers.Select(o => o.CreatedById).Concat(offers.Where(o => o.SentById is not null).Select(o => o.SentById!.Value)).Distinct().ToList();
        var names = await db.Users.Where(u => userIds.Contains(u.Id) && u.TenantId == tenantId)
            .ToDictionaryAsync(u => u.Id, u => u.FirstName + " " + u.LastName, ct);
        return offers.Select(o => new OfferItem(
            o.Id, o.ApplicationId, o.Status, o.JobTitle, o.Salary, o.Currency, o.PayPeriod, o.StartDate, o.ExpiresAt, o.Letter, o.Notes,
            names.GetValueOrDefault(o.CreatedById, ""), o.CreatedAt, o.SentAt, o.SentById is { } s ? names.GetValueOrDefault(s) : null,
            o.RespondedAt, o.SignedName, o.ResponseIp, o.DeclineReason)).ToList();
    }

    private static async Task<OfferItem> ItemAsync(CrmDbContext db, Offer offer, EffectivePermissions p, CancellationToken ct) =>
        (await ToItemsAsync(db, [offer], p.TenantId, ct))[0];

    private static async Task<string> JobTitleAsync(CrmDbContext db, CustomRecord app, CancellationToken ct) =>
        Guid.TryParse(Cf.AsText(Cf.Parse(app.CustomFields)["position"]), out var jobId)
            ? await db.CustomRecords.Where(r => r.Id == jobId).Select(r => r.Name).FirstOrDefaultAsync(ct) ?? ""
            : "";

    // ---------- Reading ----------

    private static async Task<IResult> List(Guid applicationId, Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var (_, denied) = await ApplicationAsync(db, p, applicationId, ct);
        if (denied is not null) return denied;
        var offers = await db.Offers.Where(o => o.ApplicationId == applicationId).OrderByDescending(o => o.CreatedAt).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (offers.Count(o => ExpireIfDue(o, now)) > 0) await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await ToItemsAsync(db, offers, p.TenantId, ct));
    }

    private static async Task<IResult> Defaults(Guid applicationId, Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var (app, denied) = await ApplicationAsync(db, p, applicationId, ct);
        if (denied is not null) return denied;
        var now = clock.GetUtcNow().UtcDateTime;
        var tenantId = p.TenantId;
        var currency = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Currency).FirstAsync(ct);
        decimal? expected = null;
        if (Guid.TryParse(Cf.AsText(Cf.Parse(app!.CustomFields)["candidate"]), out var candidateId) &&
            await db.CustomRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == candidateId, ct) is { } candidate &&
            decimal.TryParse(Cf.AsText(Cf.Parse(candidate.CustomFields)["expected_salary"]), NumberStyles.Number, CultureInfo.InvariantCulture, out var salary))
            expected = salary;
        var letter = await db.EmailTemplates.AsNoTracking().Where(t => t.ObjectKey == ApplicationKey && t.Name == OfferKeys.LetterTemplate)
            .Select(t => t.Body).FirstOrDefaultAsync(ct) ?? OfferKeys.DefaultLetter;
        var expires = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddDays(7);
        return TypedResults.Ok(new OfferDefaults(await JobTitleAsync(db, app, ct), expected, currency, PayPeriod.Year,
            DateOnly.FromDateTime(now).AddDays(28), expires, letter));
    }

    private static async Task<IResult> Preview(Guid id, Access access, CrmDbContext db, EmailComposer emails, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var (offer, app, denied) = await OfferAsync(db, p, id, ct);
        if (denied is not null) return denied;
        var (text, blank) = await RenderAsync(db, emails, app!, offer!, p, offer!.Letter, null, ct);
        return TypedResults.Ok(new OfferPreview(text, blank));
    }

    /// <summary>Fills in a text for this application and offer; <paramref name="link"/> is only known when sending.</summary>
    private static async Task<(string Text, List<string> Blank)> RenderAsync(
        CrmDbContext db, EmailComposer emails, CustomRecord app, Offer offer, EffectivePermissions viewer, string text, string? link, CancellationToken ct)
    {
        var fields = (await emails.FieldsAsync(db, ApplicationKey, ct))!;
        var context = await emails.ContextAsync(db, app, ApplicationKey, viewer, ct);
        foreach (var (key, value) in OfferKeys.Values(offer)) context[key] = value;
        if (link is not null) context[OfferKeys.LinkKey] = link;
        return (EmailComposer.Render(text, context), EmailComposer.BlankPlaceholders(text, fields, context).Where(b => link is not null || !b.Contains("Link to respond")).ToList());
    }

    // ---------- Drafting ----------

    private static async Task<Dictionary<string, string[]>> ValidateAsync(
        OfferRequest req, CrmDbContext db, EmailComposer emails, DateTime now, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (req.Salary <= 0 || req.Salary > MaxSalary) errors["salary"] = ["Enter the salary as a positive amount."];
        if (req.Currency is not { Length: 3 } c || !c.All(char.IsAsciiLetterUpper)) errors["currency"] = ["Use a three-letter currency code, e.g. AUD."];
        if (req.StartDate < DateOnly.FromDateTime(now)) errors["startDate"] = ["The start date can't be in the past."];
        var expires = Utc(req.ExpiresAt);
        if (expires <= now.AddHours(1) || expires > now.AddDays(MaxValidityDays))
            errors["expiresAt"] = [$"Give the candidate between an hour and {MaxValidityDays} days to respond."];
        if (string.IsNullOrWhiteSpace(req.Letter)) errors["letter"] = ["Write the offer letter."];
        else if (EmailComposer.UnknownPlaceholders(req.Letter, (await emails.FieldsAsync(db, ApplicationKey, ct))!) is { Count: > 0 } unknown)
            errors["letter"] = [$"Unknown fields: {string.Join(", ", unknown.Select(u => $"{{{{{u}}}}}"))}."];
        if (string.IsNullOrWhiteSpace(req.JobTitle)) errors["jobTitle"] = ["Enter the job title."];
        return errors;
    }

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d, DateTimeKind.Utc);

    private static void Apply(Offer offer, OfferRequest req)
    {
        offer.JobTitle = req.JobTitle!.Trim();
        offer.Salary = decimal.Round(req.Salary, 2);
        offer.Currency = req.Currency;
        offer.PayPeriod = req.PayPeriod;
        offer.StartDate = req.StartDate;
        offer.ExpiresAt = Utc(req.ExpiresAt);
        offer.Letter = req.Letter!.Trim();
        offer.Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
    }

    private static async Task<IResult> Create(OfferRequest req, Access access, CrmDbContext db, EmailComposer emails, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var (app, denied) = await ApplicationAsync(db, p, req.ApplicationId, ct);
        if (denied is not null) return denied;
        var now = clock.GetUtcNow().UtcDateTime;
        var errors = await ValidateAsync(req, db, emails, now, ct);
        if (errors.Count > 0) return Errors.Validation(errors);

        var current = await db.Offers.Where(o => o.ApplicationId == app!.Id && (o.Status == OfferStatus.Draft || o.Status == OfferStatus.Sent)).ToListAsync(ct);
        if (current.Count(o => ExpireIfDue(o, now)) > 0) await db.SaveChangesAsync(ct);
        if (current.Any(o => IsActive(o.Status))) return OpenOfferExists();

        var offer = new Offer { ApplicationId = app!.Id, CreatedById = p.UserId };
        Apply(offer, req);
        db.Offers.Add(offer);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with another request creating an offer for the same application (filtered unique index).
            return OpenOfferExists();
        }
        return TypedResults.Created($"/api/offers/{offer.Id}", await ItemAsync(db, offer, p, ct));
    }

    private static ValidationProblem OpenOfferExists() =>
        Errors.Validation("applicationId", "This application already has an open offer. Withdraw it before making a new one.");

    private static async Task<IResult> Update(Guid id, OfferRequest req, Access access, CrmDbContext db, EmailComposer emails, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var (offer, _, denied) = await OfferAsync(db, p, id, ct);
        if (denied is not null) return denied;
        if (offer!.Status != OfferStatus.Draft) return Errors.Validation("status", "Only draft offers can be edited. Withdraw it and make a new offer instead.");
        if (req.ApplicationId != offer.ApplicationId) return Errors.Validation("applicationId", "An offer can't move to another application.");
        var errors = await ValidateAsync(req, db, emails, clock.GetUtcNow().UtcDateTime, ct);
        if (errors.Count > 0) return Errors.Validation(errors);
        Apply(offer, req);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await ItemAsync(db, offer, p, ct));
    }

    private static async Task<IResult> Delete(Guid id, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var (offer, _, denied) = await OfferAsync(db, p, id, ct);
        if (denied is not null) return denied;
        if (offer!.Status != OfferStatus.Draft) return Errors.Validation("status", "Only drafts can be deleted; withdraw a sent offer instead.");
        db.Offers.Remove(offer);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---------- Sending and withdrawing ----------

    private static async Task<IResult> Send(
        Guid id, Access access, CrmDbContext db, EmailComposer emails, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var (offer, app, denied) = await OfferAsync(db, p, id, ct);
        if (denied is not null) return denied;
        var now = clock.GetUtcNow().UtcDateTime;
        if (ExpireIfDue(offer!, now)) await db.SaveChangesAsync(ct);
        if (!IsActive(offer!.Status)) return Errors.Validation("status", $"This offer is {offer.Status.ToString().ToLowerInvariant()} and can't be sent.");
        if (offer.ExpiresAt <= now.AddHours(1)) return Errors.Validation("expiresAt", "The response deadline is too close. Withdraw this offer and make a new one.");
        if (emails.PublicUrl() is not { } publicUrl) return Errors.Validation("link", "The site address (App:PublicUrl) isn't configured, so the candidate's link can't be created.");

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var link = $"{publicUrl}/offer/{token}";
        offer.TokenHash = HashToken(token);
        if (offer.Status == OfferStatus.Draft)
        {
            offer.Letter = (await RenderAsync(db, emails, app!, offer, p, offer.Letter, null, ct)).Text;
            offer.Status = OfferStatus.Sent;
        }
        offer.SentAt = now;
        offer.SentById = p.UserId;
        await MoveToOfferStageAsync(db, app!, ct);
        await db.SaveChangesAsync(ct);

        var emailNote = await EmailCandidateAsync(db, emails, app!, offer, p, link, now, ct);
        return TypedResults.Ok(new OfferSent(await ItemAsync(db, offer, p, ct), link, emailNote));
    }

    /// <summary>Moves an application still in an earlier open stage to "offer"; automation sees it as a normal stage change.</summary>
    private static async Task MoveToOfferStageAsync(CrmDbContext db, CustomRecord app, CancellationToken ct)
    {
        var stages = await db.PipelineStages.AsNoTracking().Where(s => s.ObjectKey == ApplicationKey).ToListAsync(ct);
        var target = stages.FirstOrDefault(s => s.Key == "offer" && s.Category == StageCategory.Open);
        var current = stages.FirstOrDefault(s => s.Key == app.Stage);
        if (target is null || current is not { Category: StageCategory.Open } || current.SortOrder >= target.SortOrder) return;
        app.Stage = target.Key;
    }

    private static Task<string?> EmailCandidateAsync(
        CrmDbContext db, EmailComposer emails, CustomRecord app, Offer offer, EffectivePermissions p, string link, DateTime now, CancellationToken ct) =>
        emails.QueueCandidateLinkAsync(db, app, ApplicationKey, p, new CandidateLinkEmail(
            OfferKeys.EmailTemplate, OfferKeys.DefaultEmailSubject, OfferKeys.DefaultEmailBody, OfferKeys.LinkKey, link,
            "View and respond to your offer", OfferKeys.Values(offer), "offer"), now, ct);

    private static async Task<IResult> Withdraw(Guid id, WithdrawOfferRequest req, Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var (offer, app, denied) = await OfferAsync(db, p, id, ct);
        if (denied is not null) return denied;
        var now = clock.GetUtcNow().UtcDateTime;
        ExpireIfDue(offer!, now);
        if (!IsActive(offer!.Status)) return Errors.Validation("status", $"This offer is already {offer.Status.ToString().ToLowerInvariant()}.");
        var wasSent = offer.Status == OfferStatus.Sent;
        offer.Status = OfferStatus.Withdrawn;
        offer.TokenHash = null;
        db.Activities.Add(new Activity
        {
            Type = ActivityType.Note, OwnerId = p.UserId, CustomRecordId = app!.Id, CompletedAt = now,
            Subject = wasSent ? "Offer withdrawn" : "Draft offer withdrawn",
            Description = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim(),
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await ItemAsync(db, offer, p, ct));
    }
}
