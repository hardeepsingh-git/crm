using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Crm.Api.Automation;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Metadata;
using Crm.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Customization;

public record RuleItem(
    Guid Id, string Name, string ObjectKey, AutomationTrigger Trigger, IReadOnlyList<AutomationCondition> Conditions,
    IReadOnlyList<AutomationAction> Actions, bool IsActive, int RunCount, DateTime? LastRunAt,
    string? ScheduleField, int ScheduleOffsetMinutes);

public record RuleRequest(
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(40)] string ObjectKey,
    AutomationTrigger Trigger,
    AutomationCondition[] Conditions,
    AutomationAction[] Actions,
    bool IsActive = true,
    [MaxLength(80)] string? ScheduleField = null,
    int ScheduleOffsetMinutes = 0);

public record AutomationField(string Key, string Label, bool Settable, bool IsDate = false);

public static class AutomationEndpoints
{
    public static RouteGroupBuilder MapAutomation(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/admin/automation").WithTags("Automation")
            .RequirePermission(AdminResources.Customization, PermissionActions.Manage);
        g.MapGet("/", List);
        g.MapGet("/fields/{objectKey}", Fields);
        g.MapPost("/", Create);
        g.MapPut("/{id:guid}", Update);
        g.MapDelete("/{id:guid}", Delete);
        return api;
    }

    private static async Task<Ok<List<RuleItem>>> List(CrmDbContext db, CancellationToken ct) =>
        TypedResults.Ok((await db.AutomationRules.AsNoTracking().OrderBy(r => r.ObjectKey).ThenBy(r => r.Name).ToListAsync(ct))
            .Select(ToItem).ToList());

    private static async Task<Results<Ok<List<AutomationField>>, NotFound>> Fields(string objectKey, MetadataService meta, CancellationToken ct) =>
        await FieldsAsync(objectKey, meta, ct) is { } fields ? TypedResults.Ok(fields) : TypedResults.NotFound();

    /// <summary>Fields usable in conditions, SetField actions and schedules: standard properties plus custom fields.</summary>
    public static async Task<List<AutomationField>?> FieldsAsync(string objectKey, MetadataService meta, CancellationToken ct)
    {
        var definition = await meta.ObjectAsync(objectKey, ct);
        if (definition is null) return null;
        var type = RecordStore.EntityType(definition.Key);
        var settable = RecordFields.SettableFields(type).ToHashSet();
        var dates = ScheduledAutomation.StandardDateFields(type).ToHashSet();
        var fields = RecordFields.ScalarProperties(type)
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .Where(n => n is not ("id" or "tenantId" or "objectDefinitionId"))
            .Select(n => new AutomationField(n, RecordFields.Humanize(n), settable.Contains(n), dates.Contains(n)))
            .ToList();
        fields.AddRange((await meta.FieldsAsync(objectKey, ct)).Select(f =>
            new AutomationField($"cf.{f.Key}", f.Label, true, f.Type is FieldType.Date or FieldType.DateTime)));
        return fields;
    }

    private static async Task<Results<Created<RuleItem>, ValidationProblem>> Create(
        RuleRequest req, Access access, CrmDbContext db, MetadataService meta, EmailComposer emails, CancellationToken ct)
    {
        if (await ValidateAsync(req, access, db, meta, emails, ct) is { } problem) return problem;
        var rule = new AutomationRule();
        Apply(rule, req);
        db.AutomationRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/admin/automation/{rule.Id}", ToItem(rule));
    }

    private static async Task<Results<Ok<RuleItem>, NotFound, ValidationProblem>> Update(
        Guid id, RuleRequest req, Access access, CrmDbContext db, MetadataService meta, EmailComposer emails, CancellationToken ct)
    {
        var rule = await db.AutomationRules.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return TypedResults.NotFound();
        if (await ValidateAsync(req, access, db, meta, emails, ct) is { } problem) return problem;
        Apply(rule, req);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToItem(rule));
    }

    private static async Task<Results<NoContent, NotFound>> Delete(Guid id, CrmDbContext db, CancellationToken ct)
    {
        var rule = await db.AutomationRules.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return TypedResults.NotFound();
        db.AutomationRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    public const int MaxScheduleOffsetMinutes = 365 * 24 * 60;

    private static async Task<ValidationProblem?> ValidateAsync(
        RuleRequest req, Access access, CrmDbContext db, MetadataService meta, EmailComposer emails, CancellationToken ct)
    {
        var definition = await meta.ObjectAsync(req.ObjectKey, ct);
        if (definition is null) return Errors.Validation("objectKey", "Unknown object.");
        if (req.Trigger == AutomationTrigger.StageChanged && !definition.HasPipeline)
            return Errors.Validation("trigger", "Stage changes only apply to objects with a pipeline.");
        if (req.Actions.Length == 0) return Errors.Validation("actions", "Add at least one action.");

        var fields = (await FieldsAsync(req.ObjectKey, meta, ct))!;
        if (req.Trigger == AutomationTrigger.DateReached)
        {
            if (fields.All(f => f.Key != req.ScheduleField || !f.IsDate))
                return Errors.Validation("scheduleField", "Choose a date field for the schedule.");
            if (Math.Abs(req.ScheduleOffsetMinutes) > MaxScheduleOffsetMinutes)
                return Errors.Validation("scheduleOffsetMinutes", "The offset can be at most 365 days before or after the date.");
            if (req.Conditions.Any(c => c.Operator == ConditionOperator.Changed))
                return Errors.Validation("conditions", "'Changed' only applies to rules that run when a record is saved.");
        }
        foreach (var c in req.Conditions)
            if (fields.All(f => f.Key != c.Field)) return Errors.Validation("conditions", $"Unknown field '{c.Field}'.");

        EmailFieldSet? emailFields = null;
        foreach (var a in req.Actions)
        {
            var error = a.Type switch
            {
                AutomationActionType.CreateTask when string.IsNullOrWhiteSpace(a.Subject) => "Tasks need a subject.",
                AutomationActionType.CreateTask when a.DueInDays is < 0 or > 365 => "Due in days must be between 0 and 365.",
                AutomationActionType.Notify when string.IsNullOrWhiteSpace(a.Message) => "Notifications need a message.",
                AutomationActionType.SetField when fields.All(f => f.Key != a.Field || !f.Settable) => $"Field '{a.Field}' can't be set by automation.",
                _ => null,
            };
            if (a.Type == AutomationActionType.SendEmail)
            {
                emailFields ??= await emails.FieldsAsync(db, req.ObjectKey, ct);
                error = await ValidateEmailActionAsync(a, req.ObjectKey, emailFields!, access, db, ct);
            }
            if (error is not null) return Errors.Validation("actions", error);
            if (a.AssignTo is not null and not "owner")
            {
                if (!Guid.TryParse(a.AssignTo, out var userId) ||
                    !await db.Users.AnyAsync(u => u.Id == userId && u.TenantId == access.TenantId && u.IsActive, ct))
                    return Errors.Validation("actions", "Assign to must be 'owner' or an active user.");
            }
        }
        return null;
    }

    private static async Task<string?> ValidateEmailActionAsync(
        AutomationAction a, string objectKey, EmailFieldSet fields, Access access, CrmDbContext db, CancellationToken ct)
    {
        if (a.To is not null and not "owner" && fields.Recipients.All(r => r.Key != a.To))
        {
            if (!Guid.TryParse(a.To, out var userId) ||
                !await db.Users.AnyAsync(u => u.Id == userId && u.TenantId == access.TenantId && u.IsActive, ct))
                return "Send the email to the record owner, an active user, or one of the record's email fields.";
        }
        if (a.TemplateId is { } templateId)
        {
            var templateObject = await db.EmailTemplates.Where(t => t.Id == templateId).Select(t => t.ObjectKey).FirstOrDefaultAsync(ct);
            return templateObject == objectKey ? null : "Choose an email template for this object.";
        }
        if (string.IsNullOrWhiteSpace(a.Subject) || string.IsNullOrWhiteSpace(a.Message))
            return "Emails need a template, or a subject and a message.";
        if (a.Subject.Length > EmailComposer.MaxSubject || a.Message.Length > EmailComposer.MaxBody)
            return $"Email subjects can be at most {EmailComposer.MaxSubject} characters and messages {EmailComposer.MaxBody}.";
        var unknown = EmailComposer.UnknownPlaceholders(a.Subject + " " + a.Message, fields);
        return unknown.Count == 0 ? null : $"Unknown placeholder(s): {string.Join(", ", unknown.Select(u => "{{" + u + "}}"))}.";
    }

    private static void Apply(AutomationRule rule, RuleRequest req)
    {
        rule.Name = req.Name.Trim();
        rule.ObjectKey = req.ObjectKey;
        rule.Trigger = req.Trigger;
        rule.Conditions = JsonSerializer.Serialize(req.Conditions, AutomationJson.Options);
        rule.Actions = JsonSerializer.Serialize(req.Actions, AutomationJson.Options);
        rule.IsActive = req.IsActive;
        var scheduled = req.Trigger == AutomationTrigger.DateReached;
        rule.ScheduleField = scheduled ? req.ScheduleField : null;
        rule.ScheduleOffsetMinutes = scheduled ? req.ScheduleOffsetMinutes : 0;
    }

    private static RuleItem ToItem(AutomationRule r) => new(
        r.Id, r.Name, r.ObjectKey, r.Trigger,
        JsonSerializer.Deserialize<List<AutomationCondition>>(r.Conditions, AutomationJson.Options) ?? [],
        JsonSerializer.Deserialize<List<AutomationAction>>(r.Actions, AutomationJson.Options) ?? [],
        r.IsActive, r.RunCount, r.LastRunAt, r.ScheduleField, r.ScheduleOffsetMinutes);
}
