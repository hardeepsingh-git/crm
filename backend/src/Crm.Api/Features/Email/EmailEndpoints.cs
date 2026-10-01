using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Crm.Api.Automation;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Messaging;
using Crm.Api.Platform;
using Crm.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Email;

public record EmailTemplateItem(Guid Id, string Name, string ObjectKey, string Subject, string Body, DateTime UpdatedAt);

public record EmailTemplateRequest(
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(40)] string ObjectKey,
    [Required, MaxLength(EmailComposer.MaxSubject)] string Subject,
    [Required, MaxLength(EmailComposer.MaxBody)] string Body);

public record ComposeRequest([Required, MaxLength(40)] string ObjectKey, Guid RecordId, Guid? TemplateId = null);

/// <param name="BlankFields">Fields the template uses that are empty on this record, so the text would have gaps.</param>
public record ComposeResult(IReadOnlyList<EmailRecipient> Recipients, string Subject, string Body, IReadOnlyList<string> BlankFields);

public record SendEmailRequest(
    [Required, MaxLength(40)] string ObjectKey,
    Guid RecordId,
    [Required, MaxLength(320)] string To,
    [Required, MaxLength(EmailComposer.MaxSubject)] string Subject,
    [Required, MaxLength(EmailComposer.MaxBody)] string Body,
    Guid? TemplateId = null);

public record QueuedEmail(Guid Id, OutboundEmailStatus Status);

public record OutboundEmailItem(
    Guid Id, string To, string Subject, string? ObjectKey, Guid? RecordId, string? RecordName, Guid? RuleId, string SentByName,
    OutboundEmailStatus Status, int Attempts, string? LastError, DateTime CreatedAt, DateTime? SentAt, DateTime NextAttemptAt);

public record OutboundEmailDetail(OutboundEmailItem Email, string Body, string? ReplyTo, string? RuleName);

public record EmailLogPage(PagedResult<OutboundEmailItem> Page, EmailProvider DeliveryMode, int DailyLimit, int QueuedToday);

public static class EmailEndpoints
{
    public const string SendRateLimitPolicy = "email-send";

    public static RouteGroupBuilder MapEmail(this RouteGroupBuilder api)
    {
        var mail = api.MapGroup("/email").WithTags("Email");
        mail.MapGet("/fields/{objectKey}", Fields).RequirePermission(ctx => ObjectKeyOf(ctx), PermissionActions.Read);
        mail.MapGet("/templates", TemplatesFor).RequirePermission(ctx => ObjectKeyOf(ctx), PermissionActions.Read);
        mail.MapPost("/compose", Compose);
        mail.MapPost("/send", Send).RequireRateLimiting(SendRateLimitPolicy);

        var templates = api.MapGroup("/admin/email-templates").WithTags("Email")
            .RequirePermission(AdminResources.Customization, PermissionActions.Manage);
        templates.MapGet("/", ListTemplates);
        templates.MapPost("/", CreateTemplate);
        templates.MapPut("/{id:guid}", UpdateTemplate);
        templates.MapDelete("/{id:guid}", DeleteTemplate);

        var log = api.MapGroup("/admin/emails").WithTags("Email").RequirePermission(AdminResources.Settings, PermissionActions.Manage);
        log.MapGet("/", ListEmails);
        log.MapGet("/{id:guid}", GetEmail);
        log.MapPost("/{id:guid}/retry", Retry);
        log.MapPost("/{id:guid}/cancel", Cancel);
        return api;
    }

    private static string ObjectKeyOf(HttpContext ctx) =>
        ctx.Request.RouteValues["objectKey"] as string ?? ctx.Request.Query["objectKey"].ToString();

    // ---------- Sending from a record ----------

    private static async Task<Results<Ok<EmailFieldSet>, NotFound>> Fields(string objectKey, EmailComposer emails, CrmDbContext db, CancellationToken ct) =>
        await emails.FieldsAsync(db, objectKey, ct) is { } fields ? TypedResults.Ok(fields) : TypedResults.NotFound();

    private static async Task<Ok<List<EmailTemplateItem>>> TemplatesFor(string objectKey, CrmDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.EmailTemplates.AsNoTracking().Where(t => t.ObjectKey == objectKey).OrderBy(t => t.Name)
            .Select(t => new EmailTemplateItem(t.Id, t.Name, t.ObjectKey, t.Subject, t.Body, t.UpdatedAt)).ToListAsync(ct));

    /// <summary>The record, if the caller may read it (else 404) and update it (else 403): sending an email acts on the record.</summary>
    private static async Task<(OwnedEntity? Record, IResult? Error)> SendableAsync(
        string objectKey, Guid recordId, Access access, CrmDbContext db, CancellationToken ct)
    {
        if (objectKey == ObjectKeys.Activity) return (null, TypedResults.NotFound());
        var record = await RecordStore.FindAsync(db, objectKey, recordId, ct);
        var permissions = await access.GetAsync(ct);
        if (record is null || !permissions.CanAccess(objectKey, PermissionActions.Read, record.OwnerId)) return (null, TypedResults.NotFound());
        if (!permissions.CanAccess(objectKey, PermissionActions.Update, record.OwnerId)) return (null, TypedResults.Forbid());
        return (record, null);
    }

    private static async Task<IResult> Compose(ComposeRequest req, Access access, CrmDbContext db, EmailComposer emails, CancellationToken ct)
    {
        var (record, error) = await SendableAsync(req.ObjectKey, req.RecordId, access, db, ct);
        if (record is null) return error!;
        var fields = (await emails.FieldsAsync(db, req.ObjectKey, ct))!;
        var context = await emails.ContextAsync(db, record, req.ObjectKey, await access.GetAsync(ct), ct);
        var (subject, body) = ("", "");
        List<string> blank = [];
        if (req.TemplateId is { } templateId)
        {
            var template = await db.EmailTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId && t.ObjectKey == req.ObjectKey, ct);
            if (template is null) return Errors.Validation("templateId", "Template not found for this object.");
            (subject, body) = (EmailText.SingleLine(EmailComposer.Render(template.Subject, context)), EmailComposer.Render(template.Body, context));
            blank = EmailComposer.BlankPlaceholders(template.Subject + " " + template.Body, fields, context);
        }
        return TypedResults.Ok(new ComposeResult(EmailComposer.RecipientsOf(fields, context), subject, body, blank));
    }

    private static async Task<IResult> Send(
        SendEmailRequest req, Access access, CrmDbContext db, EmailComposer emails, TimeProvider clock, CancellationToken ct)
    {
        var (record, error) = await SendableAsync(req.ObjectKey, req.RecordId, access, db, ct);
        if (record is null) return error!;
        var fields = (await emails.FieldsAsync(db, req.ObjectKey, ct))!;
        var permissions = await access.GetAsync(ct);
        var context = await emails.ContextAsync(db, record, req.ObjectKey, permissions, ct);

        // Only the record's own addresses: the CRM must not become a way to email arbitrary people.
        var to = EmailText.Address(req.To);
        if (to is null || EmailComposer.RecipientsOf(fields, context).All(r => r.Address != to))
            return Errors.Validation("to", "Choose one of the record's email addresses.");
        var unknown = EmailComposer.UnknownPlaceholders(req.Subject + " " + req.Body, fields);
        if (unknown.Count > 0)
            return Errors.Validation("body", $"Unknown placeholder(s): {string.Join(", ", unknown.Select(u => "{{" + u + "}}"))}.");
        if (req.TemplateId is { } templateId && !await db.EmailTemplates.AnyAsync(t => t.Id == templateId, ct))
            return Errors.Validation("templateId", "Template not found.");

        var now = clock.GetUtcNow().UtcDateTime;
        if (await emails.DailyLimitReachedAsync(db, now, ct))
            return TypedResults.Problem($"Your organisation has reached its daily limit of {emails.DailyLimit} emails. Try again tomorrow.",
                statusCode: StatusCodes.Status429TooManyRequests);

        var sender = await db.Users.AsNoTracking().Where(u => u.Id == access.UserId).Select(u => new { u.Email, u.FirstName, u.LastName }).SingleAsync(ct);
        var email = await emails.QueueAsync(db, new QueueEmail(
            record, req.ObjectKey, to, EmailComposer.Render(req.Subject, context), EmailComposer.Render(req.Body, context),
            TemplateId: req.TemplateId, SentById: access.UserId, SentByName: $"{sender.FirstName} {sender.LastName}".Trim(),
            ReplyTo: EmailText.Address(sender.Email)), now, ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted((string?)null, new QueuedEmail(email.Id, email.Status));
    }

    // ---------- Templates (administration) ----------

    private static async Task<Ok<List<EmailTemplateItem>>> ListTemplates(CrmDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.EmailTemplates.AsNoTracking().OrderBy(t => t.ObjectKey).ThenBy(t => t.Name)
            .Select(t => new EmailTemplateItem(t.Id, t.Name, t.ObjectKey, t.Subject, t.Body, t.UpdatedAt)).ToListAsync(ct));

    private static async Task<Results<Created<EmailTemplateItem>, ValidationProblem>> CreateTemplate(
        EmailTemplateRequest req, CrmDbContext db, EmailComposer emails, CancellationToken ct)
    {
        if (await ValidateTemplateAsync(req, null, db, emails, ct) is { } problem) return problem;
        var template = new EmailTemplate();
        Apply(template, req);
        db.EmailTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/admin/email-templates/{template.Id}", ToItem(template));
    }

    private static async Task<Results<Ok<EmailTemplateItem>, NotFound, ValidationProblem>> UpdateTemplate(
        Guid id, EmailTemplateRequest req, CrmDbContext db, EmailComposer emails, CancellationToken ct)
    {
        var template = await db.EmailTemplates.SingleOrDefaultAsync(t => t.Id == id, ct);
        if (template is null) return TypedResults.NotFound();
        if (template.ObjectKey != req.ObjectKey && (await RulesUsingAsync(db, id, ct)).Count > 0)
            return Errors.Validation("objectKey", "Automation rules use this template, so its object can't change.");
        if (await ValidateTemplateAsync(req, id, db, emails, ct) is { } problem) return problem;
        Apply(template, req);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToItem(template));
    }

    private static async Task<IResult> DeleteTemplate(Guid id, CrmDbContext db, CancellationToken ct)
    {
        var template = await db.EmailTemplates.SingleOrDefaultAsync(t => t.Id == id, ct);
        if (template is null) return TypedResults.NotFound();
        var rules = await RulesUsingAsync(db, id, ct);
        if (rules.Count > 0)
            return TypedResults.Problem($"Used by automation rule(s): {string.Join(", ", rules)}. Change those rules first.",
                statusCode: StatusCodes.Status409Conflict);
        db.EmailTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<List<string>> RulesUsingAsync(CrmDbContext db, Guid templateId, CancellationToken ct) =>
        (await db.AutomationRules.AsNoTracking().Select(r => new { r.Name, r.Actions }).ToListAsync(ct))
            .Where(r => (JsonSerializer.Deserialize<List<AutomationAction>>(r.Actions, AutomationJson.Options) ?? []).Any(a => a.TemplateId == templateId))
            .Select(r => r.Name).ToList();

    private static async Task<ValidationProblem?> ValidateTemplateAsync(
        EmailTemplateRequest req, Guid? id, CrmDbContext db, EmailComposer emails, CancellationToken ct)
    {
        var name = req.Name.Trim();
        if (name.Length == 0) return Errors.Validation("name", "Name is required.");
        if (await db.EmailTemplates.AnyAsync(t => t.Name == name && t.Id != id, ct)) return Errors.Validation("name", "A template with this name already exists.");
        if (await emails.FieldsAsync(db, req.ObjectKey, ct) is not { } fields) return Errors.Validation("objectKey", "Unknown object.");
        if (string.IsNullOrWhiteSpace(req.Subject)) return Errors.Validation("subject", "Subject is required.");
        if (string.IsNullOrWhiteSpace(req.Body)) return Errors.Validation("body", "Message is required.");
        var unknown = EmailComposer.UnknownPlaceholders(req.Subject + " " + req.Body, fields);
        return unknown.Count == 0 ? null
            : Errors.Validation("body", $"Unknown placeholder(s): {string.Join(", ", unknown.Select(u => "{{" + u + "}}"))}.");
    }

    private static void Apply(EmailTemplate template, EmailTemplateRequest req)
    {
        template.Name = req.Name.Trim();
        template.ObjectKey = req.ObjectKey;
        template.Subject = EmailText.SingleLine(req.Subject);
        template.Body = req.Body.Replace("\r\n", "\n");
    }

    private static EmailTemplateItem ToItem(EmailTemplate t) => new(t.Id, t.Name, t.ObjectKey, t.Subject, t.Body, t.UpdatedAt);

    // ---------- Email log (administration) ----------

    private static async Task<Ok<EmailLogPage>> ListEmails(
        OutboundEmailStatus? status, string? search, int? page, int? pageSize,
        CrmDbContext db, EmailComposer emails, PlatformSettingsService settings, TimeProvider clock, CancellationToken ct)
    {
        var query = db.OutboundEmails.AsNoTracking();
        if (status is not null) query = query.Where(e => e.Status == status);
        if (QueryableExtensions.NormalizeSearch(search) is { } term)
            query = query.Where(e => e.To.ToLower().Contains(term) || e.Subject.ToLower().Contains(term) || (e.RecordName ?? "").ToLower().Contains(term));
        var result = await query.OrderByDescending(e => e.CreatedAt).Select(e => new OutboundEmailItem(
                e.Id, e.To, e.Subject, e.ObjectKey, e.RecordId, e.RecordName, e.RuleId, e.SentByName, e.Status, e.Attempts, e.LastError,
                e.CreatedAt, e.SentAt, e.NextAttemptAt))
            .ToPagedAsync(page ?? 1, pageSize ?? 25, ct);
        var today = clock.GetUtcNow().UtcDateTime.Date;
        var queuedToday = await db.OutboundEmails.CountAsync(e => e.CreatedAt >= today && EmailComposer.CountsTowardLimit.Contains(e.Status), ct);
        return TypedResults.Ok(new EmailLogPage(result, (await settings.GetAsync(ct)).EmailProvider, emails.DailyLimit, queuedToday));
    }

    private static async Task<Results<Ok<OutboundEmailDetail>, NotFound>> GetEmail(Guid id, CrmDbContext db, CancellationToken ct)
    {
        var e = await db.OutboundEmails.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return TypedResults.NotFound();
        var ruleName = e.RuleId is null ? null : await db.AutomationRules.Where(r => r.Id == e.RuleId).Select(r => r.Name).FirstOrDefaultAsync(ct);
        return TypedResults.Ok(new OutboundEmailDetail(ToItem(e), e.Body, e.ReplyTo, ruleName));
    }

    private static async Task<IResult> Retry(Guid id, CrmDbContext db, EmailComposer emails, TimeProvider clock, CancellationToken ct)
    {
        var email = await db.OutboundEmails.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (email is null) return TypedResults.NotFound();
        if (email.Status != OutboundEmailStatus.Failed) return Errors.Validation("status", "Only failed emails can be retried.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (await emails.DailyLimitReachedAsync(db, now, ct))
            return TypedResults.Problem($"Your organisation has reached its daily limit of {emails.DailyLimit} emails.", statusCode: StatusCodes.Status429TooManyRequests);

        (email.Status, email.Attempts, email.NextAttemptAt, email.LockedUntil, email.LastError) = (OutboundEmailStatus.Pending, 0, now, null, null);
        if (email.ActivityId is { } activityId && await db.Activities.FirstOrDefaultAsync(a => a.Id == activityId, ct) is { } activity
            && activity.Subject.StartsWith(EmailDispatcher.NotSent, StringComparison.Ordinal))
        {
            activity.Subject = activity.Subject[EmailDispatcher.NotSent.Length..];
            var blank = activity.Description?.IndexOf("\n\n", StringComparison.Ordinal) ?? -1;
            if (blank >= 0) activity.Description = activity.Description![(blank + 2)..];
        }
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToItem(email));
    }

    private static async Task<IResult> Cancel(Guid id, CrmDbContext db, EmailDispatcher dispatcher, TenantContext tenant, CancellationToken ct)
    {
        var email = await db.OutboundEmails.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (email is null) return TypedResults.NotFound();
        if (email.Status != OutboundEmailStatus.Pending) return Errors.Validation("status", "Only emails waiting to be sent can be cancelled.");
        email.Status = OutboundEmailStatus.Cancelled;
        email.LastError = $"Cancelled by {tenant.UserName ?? "an administrator"}.";
        await dispatcher.MarkNotSentAsync(email, ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToItem(email));
    }

    private static OutboundEmailItem ToItem(OutboundEmail e) => new(
        e.Id, e.To, e.Subject, e.ObjectKey, e.RecordId, e.RecordName, e.RuleId, e.SentByName, e.Status, e.Attempts, e.LastError,
        e.CreatedAt, e.SentAt, e.NextAttemptAt);
}
