using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Features.MockInterviews;
using Crm.Api.Features.Offers;
using Crm.Api.Messaging;
using Crm.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Automation;

public record EmailField(string Key, string Label);

/// <summary>What an email for one object can use: placeholders for its text, and fields that hold recipient addresses.</summary>
public record EmailFieldSet(IReadOnlyList<EmailField> Placeholders, IReadOnlyList<EmailField> Recipients);

public record EmailRecipient(string Label, string Address);

/// <summary>A template email to an application's candidate carrying a personal link, e.g. an offer or a mock interview.</summary>
/// <param name="What">Used in notes to the sender, e.g. "offer".</param>
public record CandidateLinkEmail(
    string TemplateName, string DefaultSubject, string DefaultBody, string LinkKey, string Link, string LinkLine,
    IEnumerable<KeyValuePair<string, object?>> Values, string What);

public record QueueEmail(
    OwnedEntity Record, string ObjectKey, string To, string Subject, string Body,
    Guid? RuleId = null, Guid? TemplateId = null, Guid? SentById = null, string SentByName = "Automation", string? ReplyTo = null,
    string? TimelineBody = null);

/// <summary>Loads any record by object key; standard objects have their own tables, custom ones share <c>CustomRecords</c>.</summary>
public static class RecordStore
{
    public static async Task<OwnedEntity?> FindAsync(CrmDbContext db, string objectKey, Guid id, CancellationToken ct) => objectKey switch
    {
        ObjectKeys.Account => await db.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct),
        ObjectKeys.Contact => await db.Contacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct),
        ObjectKeys.Lead => await db.Leads.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct),
        ObjectKeys.Opportunity => await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct),
        ObjectKeys.Activity => await db.Activities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct),
        _ => await db.CustomRecords.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ObjectDefinition!.Key == objectKey, ct),
    };

    public static Type EntityType(string objectKey) => objectKey switch
    {
        ObjectKeys.Account => typeof(Account),
        ObjectKeys.Contact => typeof(Contact),
        ObjectKeys.Lead => typeof(Lead),
        ObjectKeys.Opportunity => typeof(Opportunity),
        ObjectKeys.Activity => typeof(Activity),
        _ => typeof(CustomRecord),
    };
}

/// <summary>Builds email text from records and queues it in the outbox.</summary>
public sealed partial class EmailComposer(IConfiguration config, IHostEnvironment env)
{
    public const int MaxSubject = 300;
    public const int MaxBody = 10000;

    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_.]+)\s*\}\}")]
    private static partial Regex Placeholder();

    /// <summary>Placeholders a text uses that the object doesn't offer (usually typos).</summary>
    public static List<string> UnknownPlaceholders(string? text, EmailFieldSet fields) =>
        Used(text).Where(key => fields.Placeholders.All(p => p.Key != key)).ToList();

    /// <summary>Labels of the placeholders a text uses that are blank for this record, e.g. an interview date not set yet.</summary>
    public static List<string> BlankPlaceholders(string? text, EmailFieldSet fields, IReadOnlyDictionary<string, object?> context) =>
        Used(text).Where(key => RecordFields.Format(context.GetValueOrDefault(key)).Length == 0)
            .Select(key => fields.Placeholders.FirstOrDefault(p => p.Key == key)?.Label ?? key).ToList();

    private static IEnumerable<string> Used(string? text) =>
        Placeholder().Matches(text ?? "").Select(m => m.Groups[1].Value).Distinct();

    public static string Render(string template, IReadOnlyDictionary<string, object?> context) =>
        Placeholder().Replace(template, m => RecordFields.Format(context.GetValueOrDefault(m.Groups[1].Value)));

    public string? PublicUrl()
    {
        var configured = config["App:PublicUrl"];
        if (string.IsNullOrWhiteSpace(configured) && env.IsDevelopment()) configured = "http://localhost:4200";
        return string.IsNullOrWhiteSpace(configured) ? null : configured.TrimEnd('/');
    }

    // ---------- Field catalogue ----------

    public async Task<EmailFieldSet?> FieldsAsync(CrmDbContext db, string objectKey, CancellationToken ct)
    {
        var objects = await db.ObjectDefinitions.AsNoTracking().ToDictionaryAsync(o => o.Key, ct);
        if (!objects.TryGetValue(objectKey, out var obj)) return null;
        var fields = (await db.FieldDefinitions.AsNoTracking().OrderBy(f => f.SortOrder).ThenBy(f => f.Label).ToListAsync(ct))
            .GroupBy(f => f.ObjectKey).ToDictionary(g => g.Key, g => g.ToList());
        var own = fields.GetValueOrDefault(objectKey) ?? [];

        var placeholders = new List<EmailField> { new("name", obj.NameLabel) };
        placeholders.AddRange(StandardFields(RecordStore.EntityType(objectKey))
            .Where(p => p.Key != "name").Select(p => new EmailField(p.Key, p.Label)));
        if (obj.HasPipeline) placeholders.Add(new("stage.label", "Stage"));
        var recipients = new List<EmailField>();
        if (HasStandardEmail(objectKey)) recipients.Add(new("email", "Email"));

        foreach (var f in own)
        {
            placeholders.Add(new($"cf.{f.Key}", f.Label));
            if (f.Type == FieldType.Email) recipients.Add(new($"cf.{f.Key}", f.Label));
            if (f.Type != FieldType.Lookup || f.LookupObjectKey is null || !objects.TryGetValue(f.LookupObjectKey, out var target)) continue;

            placeholders.Add(new($"cf.{f.Key}.name", $"{f.Label} → {target.NameLabel}"));
            if (HasStandardEmail(target.Key))
            {
                placeholders.Add(new($"cf.{f.Key}.email", $"{f.Label} → Email"));
                recipients.Add(new($"cf.{f.Key}.email", $"{f.Label} → Email"));
            }
            foreach (var t in (fields.GetValueOrDefault(target.Key) ?? []).Where(t => t.Type != FieldType.Lookup))
            {
                placeholders.Add(new($"cf.{f.Key}.cf.{t.Key}", $"{f.Label} → {t.Label}"));
                if (t.Type == FieldType.Email) recipients.Add(new($"cf.{f.Key}.cf.{t.Key}", $"{f.Label} → {t.Label}"));
            }
        }
        placeholders.Add(new("owner.name", "Owner name"));
        placeholders.Add(new("owner.email", "Owner email"));
        placeholders.Add(new("organisation.name", "Organisation name"));
        placeholders.Add(new("link", "Link to the record"));
        if (objectKey == OfferKeys.ApplicationKey)
        {
            placeholders.AddRange(OfferKeys.Placeholders);
            placeholders.AddRange(MockKeys.Placeholders);
        }
        return new EmailFieldSet(placeholders, recipients);
    }

    private static bool HasStandardEmail(string objectKey) => objectKey is ObjectKeys.Account or ObjectKeys.Contact or ObjectKeys.Lead;

    private static IEnumerable<EmailField> StandardFields(Type type) =>
        RecordFields.ScalarProperties(type)
            .Where(p => (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType) != typeof(Guid) && p.Name != nameof(AuditableEntity.TenantId))
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .Select(key => new EmailField(key, RecordFields.Humanize(key)));

    // ---------- Values ----------

    /// <summary>
    /// Placeholder values for a record. Lookups resolve to the linked record's name, and its fields are offered as
    /// <c>cf.&lt;lookup&gt;.&lt;field&gt;</c>. With a <paramref name="viewer"/>, linked records they can't read stay blank.
    /// </summary>
    public async Task<Dictionary<string, object?>> ContextAsync(
        CrmDbContext db, OwnedEntity record, string objectKey, EffectivePermissions? viewer, CancellationToken ct)
    {
        var tenantId = db.CurrentTenantId;
        var tenant = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => new { t.Name, t.Currency }).FirstOrDefaultAsync(ct);
        var currency = tenant?.Currency ?? "";
        var definitions = (await db.FieldDefinitions.AsNoTracking().ToListAsync(ct)).GroupBy(f => f.ObjectKey).ToDictionary(g => g.Key, g => g.ToList());
        var own = definitions.GetValueOrDefault(objectKey) ?? [];

        var context = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddRecord(context, "", record, own, currency);
        if (record is IStaged staged)
            context["stage.label"] = await db.PipelineStages.AsNoTracking()
                .Where(s => s.ObjectKey == objectKey && s.Key == staged.Stage).Select(s => s.Label).FirstOrDefaultAsync(ct) ?? staged.Stage;

        var owner = await db.Users.AsNoTracking().Where(u => u.Id == record.OwnerId && u.TenantId == tenantId)
            .Select(u => new { u.FirstName, u.LastName, u.Email }).FirstOrDefaultAsync(ct);
        context["owner.name"] = owner is null ? "" : $"{owner.FirstName} {owner.LastName}".Trim();
        context["owner.email"] = owner?.Email ?? "";
        context["organisation.name"] = tenant?.Name ?? "";
        context["link"] = PublicUrl() is { } url ? url + AutomationEngine.LinkFor(record, objectKey) : "";
        if (objectKey == OfferKeys.ApplicationKey)
        {
            var offer = await db.Offers.AsNoTracking().Where(o => o.ApplicationId == record.Id && o.Status != OfferStatus.Withdrawn)
                .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);
            foreach (var (key, value) in OfferKeys.Values(offer)) context[key] = value;
            var mock = await db.MockInterviews.AsNoTracking().Where(m => m.ApplicationId == record.Id && m.Status != MockInterviewStatus.Withdrawn)
                .OrderByDescending(m => m.CreatedAt).FirstOrDefaultAsync(ct);
            foreach (var (key, value) in MockKeys.Values(mock)) context[key] = value;
        }

        var values = Cf.Parse(record.CustomFields);
        foreach (var lookup in own.Where(f => f.Type == FieldType.Lookup && f.LookupObjectKey is not null))
        {
            context[$"cf.{lookup.Key}"] = "";
            if (!Guid.TryParse(Cf.AsText(values[lookup.Key]), out var targetId)) continue;
            var target = await RecordStore.FindAsync(db, lookup.LookupObjectKey!, targetId, ct);
            if (target is null || (viewer is not null && !viewer.CanAccess(lookup.LookupObjectKey!, PermissionActions.Read, target.OwnerId))) continue;
            context[$"cf.{lookup.Key}"] = CrmDbContext.DisplayName(target);
            AddRecord(context, $"cf.{lookup.Key}.", target, definitions.GetValueOrDefault(lookup.LookupObjectKey!) ?? [], currency);
        }
        return context;
    }

    private static void AddRecord(Dictionary<string, object?> context, string prefix, OwnedEntity record, List<FieldDefinition> fields, string currency)
    {
        foreach (var (key, value) in RecordFields.Read(record).Where(kv => !kv.Key.StartsWith("cf.", StringComparison.Ordinal)))
            context[prefix + key] = value;
        context[prefix + "name"] = CrmDbContext.DisplayName(record);
        var values = Cf.Parse(record.CustomFields);
        foreach (var f in fields)
            context[$"{prefix}cf.{f.Key}"] = f.Type == FieldType.Lookup ? "" : FormatValue(f, values[f.Key], currency);
    }

    /// <summary>Human-readable custom field values: dates as "5 Oct 2026", amounts with the tenant currency, yes/no.</summary>
    public static string FormatValue(FieldDefinition field, JsonNode? node, string currency)
    {
        var text = Cf.AsText(node);
        if (string.IsNullOrEmpty(text)) return "";
        var inv = CultureInfo.InvariantCulture;
        return field.Type switch
        {
            FieldType.Date when DateOnly.TryParse(text[..Math.Min(10, text.Length)], inv, out var d) => d.ToString("d MMM yyyy", inv),
            FieldType.DateTime when DateTimeOffset.TryParse(text, inv, DateTimeStyles.AssumeUniversal, out var dt) =>
                dt.UtcDateTime.ToString("d MMM yyyy HH:mm", inv) + " UTC",
            FieldType.Currency when decimal.TryParse(text, NumberStyles.Number, inv, out var amount) => $"{currency} {amount.ToString("N2", inv)}".Trim(),
            FieldType.Number when decimal.TryParse(text, NumberStyles.Number, inv, out var n) => n.ToString("0.##", inv),
            FieldType.Boolean when bool.TryParse(text, out var b) => b ? "Yes" : "No",
            _ => text,
        };
    }

    // ---------- Recipients ----------

    /// <summary>The distinct addresses a record offers through its email fields (including linked records).</summary>
    public static List<EmailRecipient> RecipientsOf(EmailFieldSet fields, IReadOnlyDictionary<string, object?> context) =>
        fields.Recipients
            .Select(f => (f.Label, Address: EmailText.Address(RecordFields.Format(context.GetValueOrDefault(f.Key)))))
            .Where(x => x.Address is not null)
            .DistinctBy(x => x.Address)
            .Select(x => new EmailRecipient(x.Label, x.Address!))
            .ToList();

    /// <summary>Automation recipients: <c>owner</c> (default), a user id in the tenant, or an email field path.</summary>
    public static async Task<string?> ResolveRecipientAsync(
        CrmDbContext db, string? to, IReadOnlyDictionary<string, object?> context, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(to) || to == "owner") return EmailText.Address(context.GetValueOrDefault("owner.email") as string);
        if (Guid.TryParse(to, out var userId))
        {
            var tenantId = db.CurrentTenantId;
            return EmailText.Address(await db.Users.Where(u => u.Id == userId && u.TenantId == tenantId && u.IsActive).Select(u => u.Email).FirstOrDefaultAsync(ct));
        }
        return EmailText.Address(RecordFields.Format(context.GetValueOrDefault(to)));
    }

    // ---------- Outbox ----------

    public int DailyLimit => config.GetValue("Email:DailyLimitPerTenant", 1000);

    /// <summary>Emails sent or waiting to be sent; failed and cancelled ones don't use up the allowance.</summary>
    public static readonly OutboundEmailStatus[] CountsTowardLimit = [OutboundEmailStatus.Pending, OutboundEmailStatus.Sent];

    public async Task<bool> DailyLimitReachedAsync(CrmDbContext db, DateTime now, CancellationToken ct) =>
        await db.OutboundEmails.CountAsync(e => e.CreatedAt >= now.Date && CountsTowardLimit.Contains(e.Status), ct) >= DailyLimit;

    /// <summary>
    /// Adds the email to the outbox, in the caller's unit of work, and logs it as an Email activity on the record.
    /// Over the tenant's daily limit the row is stored as failed instead, so the attempt stays visible in the email log.
    /// </summary>
    public async Task<OutboundEmail> QueueAsync(CrmDbContext db, QueueEmail q, DateTime now, CancellationToken ct)
    {
        var subject = Truncate(EmailText.SingleLine(q.Subject), MaxSubject);
        var body = Truncate(q.Body, MaxBody);
        var email = new OutboundEmail
        {
            To = q.To, ReplyTo = q.ReplyTo, Subject = subject, Body = body, ObjectKey = q.ObjectKey, RecordId = q.Record.Id,
            RecordName = Truncate(CrmDbContext.DisplayName(q.Record) ?? "", 300), RuleId = q.RuleId, TemplateId = q.TemplateId,
            SentById = q.SentById, SentByName = q.SentByName, CreatedAt = now, NextAttemptAt = now,
        };
        if (await DailyLimitReachedAsync(db, now, ct))
        {
            email.Status = OutboundEmailStatus.Failed;
            email.LastError = $"Not sent: the organisation's daily limit of {DailyLimit} emails was reached.";
        }
        else if (q.Record is not Activity)
        {
            var activity = new Activity
            {
                Type = ActivityType.Email,
                Subject = Truncate(subject, 200),
                Description = Truncate($"To: {q.To}\n\n{q.TimelineBody ?? body}", 4000),
                CompletedAt = now,
                OwnerId = q.SentById ?? q.Record.OwnerId,
            };
            AutomationEngine.Link(activity, q.Record);
            db.Activities.Add(activity);
            email.ActivityId = activity.Id;
        }
        db.OutboundEmails.Add(email);
        return email;
    }

    /// <summary>
    /// Queues <paramref name="e"/> to the application's candidate from the sender. The record's timeline gets a copy without
    /// the link, which answers for the candidate. Returns a note for the sender when it couldn't be queued.
    /// </summary>
    public async Task<string?> QueueCandidateLinkAsync(
        CrmDbContext db, CustomRecord app, string objectKey, EffectivePermissions sender, CandidateLinkEmail e, DateTime now, CancellationToken ct)
    {
        var fields = (await FieldsAsync(db, objectKey, ct))!;
        var fresh = await db.CustomRecords.AsNoTracking().SingleAsync(r => r.Id == app.Id, ct);
        var context = await ContextAsync(db, fresh, objectKey, sender, ct);
        foreach (var (key, value) in e.Values) context[key] = value;
        var to = RecipientsOf(fields, context).FirstOrDefault()?.Address;
        if (to is null) return $"The candidate has no email address, so the {e.What} wasn't emailed. Share the link with them another way.";
        if (await DailyLimitReachedAsync(db, now, ct))
            return $"Your organisation's daily email limit was reached, so the {e.What} wasn't emailed. Share the link with them another way.";

        var template = await db.EmailTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.ObjectKey == objectKey && t.Name == e.TemplateName, ct);
        var subject = template?.Subject ?? e.DefaultSubject;
        var body = template?.Body ?? e.DefaultBody;
        var token = "{{" + e.LinkKey + "}}";
        if (!body.Contains(token, StringComparison.Ordinal)) body += $"\n\n{e.LinkLine}: {token}";

        var timeline = Render(body, new Dictionary<string, object?>(context) { [e.LinkKey] = "[link sent to the candidate]" });
        context[e.LinkKey] = e.Link;
        var user = await db.Users.AsNoTracking().Where(u => u.Id == sender.UserId).Select(u => new { u.Email, u.FirstName, u.LastName }).SingleAsync(ct);
        await QueueAsync(db, new QueueEmail(fresh, objectKey, to, Render(subject, context), Render(body, context),
            TemplateId: template?.Id, SentById: sender.UserId, SentByName: $"{user.FirstName} {user.LastName}".Trim(),
            ReplyTo: EmailText.Address(user.Email), TimelineBody: timeline), now, ct);
        await db.SaveChangesAsync(ct);
        return null;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
