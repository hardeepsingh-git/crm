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
    IReadOnlyList<AutomationAction> Actions, bool IsActive, int RunCount, DateTime? LastRunAt);

public record RuleRequest(
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(40)] string ObjectKey,
    AutomationTrigger Trigger,
    AutomationCondition[] Conditions,
    AutomationAction[] Actions,
    bool IsActive = true);

public record AutomationField(string Key, string Label, bool Settable);

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

    /// <summary>Fields usable in conditions and SetField actions: standard properties plus custom fields.</summary>
    public static async Task<List<AutomationField>?> FieldsAsync(string objectKey, MetadataService meta, CancellationToken ct)
    {
        var definition = await meta.ObjectAsync(objectKey, ct);
        if (definition is null) return null;
        var type = EntityType(definition);
        var settable = RecordFields.SettableFields(type).ToHashSet();
        var fields = RecordFields.ScalarProperties(type)
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .Where(n => n is not ("id" or "tenantId" or "objectDefinitionId"))
            .Select(n => new AutomationField(n, Humanize(n), settable.Contains(n)))
            .ToList();
        fields.AddRange((await meta.FieldsAsync(objectKey, ct)).Select(f => new AutomationField($"cf.{f.Key}", f.Label, true)));
        return fields;
    }

    private static Type EntityType(ObjectDefinition d) => d.Key switch
    {
        ObjectKeys.Account => typeof(Account),
        ObjectKeys.Contact => typeof(Contact),
        ObjectKeys.Lead => typeof(Lead),
        ObjectKeys.Opportunity => typeof(Opportunity),
        ObjectKeys.Activity => typeof(Activity),
        _ => typeof(CustomRecord),
    };

    private static async Task<Results<Created<RuleItem>, ValidationProblem>> Create(
        RuleRequest req, Access access, CrmDbContext db, MetadataService meta, CancellationToken ct)
    {
        if (await ValidateAsync(req, access, db, meta, ct) is { } problem) return problem;
        var rule = new AutomationRule();
        Apply(rule, req);
        db.AutomationRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/admin/automation/{rule.Id}", ToItem(rule));
    }

    private static async Task<Results<Ok<RuleItem>, NotFound, ValidationProblem>> Update(
        Guid id, RuleRequest req, Access access, CrmDbContext db, MetadataService meta, CancellationToken ct)
    {
        var rule = await db.AutomationRules.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return TypedResults.NotFound();
        if (await ValidateAsync(req, access, db, meta, ct) is { } problem) return problem;
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

    private static async Task<ValidationProblem?> ValidateAsync(RuleRequest req, Access access, CrmDbContext db, MetadataService meta, CancellationToken ct)
    {
        var definition = await meta.ObjectAsync(req.ObjectKey, ct);
        if (definition is null) return Errors.Validation("objectKey", "Unknown object.");
        if (req.Trigger == AutomationTrigger.StageChanged && !definition.HasPipeline)
            return Errors.Validation("trigger", "Stage changes only apply to objects with a pipeline.");
        if (req.Actions.Length == 0) return Errors.Validation("actions", "Add at least one action.");

        var fields = (await FieldsAsync(req.ObjectKey, meta, ct))!;
        foreach (var c in req.Conditions)
            if (fields.All(f => f.Key != c.Field)) return Errors.Validation("conditions", $"Unknown field '{c.Field}'.");

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

    private static void Apply(AutomationRule rule, RuleRequest req)
    {
        rule.Name = req.Name.Trim();
        rule.ObjectKey = req.ObjectKey;
        rule.Trigger = req.Trigger;
        rule.Conditions = JsonSerializer.Serialize(req.Conditions, AutomationJson.Options);
        rule.Actions = JsonSerializer.Serialize(req.Actions, AutomationJson.Options);
        rule.IsActive = req.IsActive;
    }

    private static RuleItem ToItem(AutomationRule r) => new(
        r.Id, r.Name, r.ObjectKey, r.Trigger,
        JsonSerializer.Deserialize<List<AutomationCondition>>(r.Conditions, AutomationJson.Options) ?? [],
        JsonSerializer.Deserialize<List<AutomationAction>>(r.Actions, AutomationJson.Options) ?? [],
        r.IsActive, r.RunCount, r.LastRunAt);

    private static string Humanize(string camel) =>
        char.ToUpperInvariant(camel[0]) + string.Concat(camel[1..].Select(c => char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
}
