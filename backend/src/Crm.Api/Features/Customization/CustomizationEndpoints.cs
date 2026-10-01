using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Crm.Api.Auth;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Metadata;
using Crm.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Customization;

public record FieldDto(
    Guid Id, string Key, string Label, FieldType Type, bool IsRequired, string[] Options, string? LookupObjectKey,
    int? MaxLength, decimal? Min, decimal? Max, string? HelpText, bool ShowInList, int SortOrder, bool IsUnique);

public record StageDto(string Key, string Label, int Probability, StageCategory Category, string Color);

public record ObjectDto(
    string Key, string Label, string PluralLabel, string Icon, string? Description, string NameLabel,
    bool IsStandard, bool IsActive, bool HasPipeline, int SortOrder, IReadOnlyList<FieldDto> Fields, IReadOnlyList<StageDto> Stages);

public record MetadataDto(TenantInfo Tenant, IReadOnlyList<ObjectDto> Objects, Dictionary<string, string[]> Picklists);

public record ObjectRequest(
    [MaxLength(40)] string? Key,
    [Required, MaxLength(100)] string Label,
    [Required, MaxLength(100)] string PluralLabel,
    [Required, MaxLength(64)] string Icon,
    [MaxLength(1000)] string? Description,
    [Required, MaxLength(100)] string NameLabel,
    bool HasPipeline,
    bool IsActive = true,
    int SortOrder = 50);

public record FieldRequest(
    [Required, MaxLength(40)] string Key,
    [Required, MaxLength(100)] string Label,
    FieldType Type,
    bool IsRequired,
    string[]? Options,
    [MaxLength(40)] string? LookupObjectKey,
    [Range(1, 4000)] int? MaxLength,
    decimal? Min,
    decimal? Max,
    [MaxLength(500)] string? HelpText,
    bool ShowInList,
    int SortOrder,
    bool IsUnique = false);

public record StagesRequest(StageDto[] Stages);

public record PicklistRequest(string[] Options);

public static partial class CustomizationEndpoints
{
    private const string Manage = PermissionActions.Manage;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    public static RouteGroupBuilder MapCustomization(this RouteGroupBuilder api)
    {
        api.MapGet("/metadata", GetMetadata).WithTags("Metadata");

        var g = api.MapGroup("/admin").WithTags("Customization").RequirePermission(AdminResources.Customization, Manage);
        g.MapPost("/objects", CreateObject);
        g.MapPut("/objects/{key}", UpdateObject);
        g.MapDelete("/objects/{key}", DeleteObject);
        g.MapPost("/objects/{key}/fields", CreateField);
        g.MapPut("/objects/{key}/fields/{fieldKey}", UpdateField);
        g.MapDelete("/objects/{key}/fields/{fieldKey}", DeleteField);
        g.MapPut("/objects/{key}/stages", ReplaceStages);
        g.MapPut("/picklists/{key}", UpdatePicklist);
        return api;
    }

    private static async Task<Ok<MetadataDto>> GetMetadata(Access access, CrmDbContext db, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == access.TenantId, ct);
        var objects = await db.ObjectDefinitions.AsNoTracking().OrderBy(o => o.SortOrder).ThenBy(o => o.Label).ToListAsync(ct);
        var fields = await db.FieldDefinitions.AsNoTracking().OrderBy(f => f.SortOrder).ThenBy(f => f.Label).ToListAsync(ct);
        var stages = await db.PipelineStages.AsNoTracking().OrderBy(s => s.SortOrder).ToListAsync(ct);
        var picklists = await db.Picklists.AsNoTracking().ToListAsync(ct);

        return TypedResults.Ok(new MetadataDto(
            new TenantInfo(tenant.Id, tenant.Name, tenant.Slug, tenant.Currency, tenant.Template),
            objects.Select(o => ToDto(o, fields.Where(f => f.ObjectKey == o.Key), stages.Where(s => s.ObjectKey == o.Key))).ToList(),
            picklists.ToDictionary(p => p.Key, p => Cf.ParseStrings(p.Options))));
    }

    // ---------- Objects ----------

    private static async Task<Results<Created<ObjectDto>, ValidationProblem>> CreateObject(ObjectRequest req, CrmDbContext db, CancellationToken ct)
    {
        var key = req.Key?.Trim().ToLowerInvariant() ?? "";
        if (!MetadataService.KeyPattern().IsMatch(key))
            return Errors.Validation("key", "Use 2-40 lowercase letters, digits or underscores, starting with a letter.");
        if (ObjectKeys.Standard.Contains(key) || AdminResources.All.Contains(key) || key is "records" or "admin" or "metadata")
            return Errors.Validation("key", "This key is reserved.");
        if (await db.ObjectDefinitions.AnyAsync(o => o.Key == key, ct)) return Errors.Validation("key", "An object with this key already exists.");

        var definition = new ObjectDefinition
        {
            Key = key, Label = req.Label.Trim(), PluralLabel = req.PluralLabel.Trim(), Icon = req.Icon.Trim(), Description = req.Description,
            NameLabel = req.NameLabel.Trim(), HasPipeline = req.HasPipeline, IsActive = req.IsActive, SortOrder = req.SortOrder,
        };
        db.ObjectDefinitions.Add(definition);
        if (req.HasPipeline) AddDefaultStages(db, key);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/admin/objects/{key}", await LoadObjectAsync(db, key, ct));
    }

    private static async Task<Results<Ok<ObjectDto>, NotFound, ValidationProblem>> UpdateObject(
        string key, ObjectRequest req, CrmDbContext db, CancellationToken ct)
    {
        var definition = await db.ObjectDefinitions.SingleOrDefaultAsync(o => o.Key == key, ct);
        if (definition is null) return TypedResults.NotFound();
        if (definition.IsStandard && req.HasPipeline != definition.HasPipeline)
            return Errors.Validation("hasPipeline", "Standard objects keep their built-in pipeline setting.");

        definition.Label = req.Label.Trim();
        definition.PluralLabel = req.PluralLabel.Trim();
        definition.Icon = req.Icon.Trim();
        definition.Description = req.Description;
        definition.NameLabel = req.NameLabel.Trim();
        definition.IsActive = req.IsActive;
        definition.SortOrder = req.SortOrder;
        if (req.HasPipeline && !definition.HasPipeline && !await db.PipelineStages.AnyAsync(s => s.ObjectKey == key, ct))
            AddDefaultStages(db, key);
        definition.HasPipeline = req.HasPipeline;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await LoadObjectAsync(db, key, ct));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, Conflict<string>>> DeleteObject(string key, CrmDbContext db, CancellationToken ct)
    {
        var definition = await db.ObjectDefinitions.SingleOrDefaultAsync(o => o.Key == key, ct);
        if (definition is null) return TypedResults.NotFound();
        if (definition.IsStandard) return Errors.Validation("key", "Standard objects can be deactivated but not deleted.");
        var count = await db.CustomRecords.CountAsync(r => r.ObjectDefinitionId == definition.Id, ct);
        if (count > 0) return TypedResults.Conflict($"{definition.PluralLabel} has {count} records. Delete them first.");

        db.FieldDefinitions.RemoveRange(await db.FieldDefinitions.Where(f => f.ObjectKey == key).ToListAsync(ct));
        db.PipelineStages.RemoveRange(await db.PipelineStages.Where(s => s.ObjectKey == key).ToListAsync(ct));
        db.AutomationRules.RemoveRange(await db.AutomationRules.Where(r => r.ObjectKey == key).ToListAsync(ct));
        var roleIds = await db.Roles.Select(r => r.Id).ToListAsync(ct);
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(p => roleIds.Contains(p.RoleId) && p.Resource == key).ToListAsync(ct));
        db.ObjectDefinitions.Remove(definition);
        await db.SaveChangesAsync(ct);
        PermissionService.Invalidate(definition.TenantId);
        return TypedResults.NoContent();
    }

    private static void AddDefaultStages(CrmDbContext db, string key)
    {
        StageDto[] defaults =
        [
            new("new", "New", 10, StageCategory.Open, "#1e88e5"),
            new("in_progress", "In Progress", 50, StageCategory.Open, "#f9a825"),
            new("done", "Done", 100, StageCategory.Won, "#2e7d32"),
            new("cancelled", "Cancelled", 0, StageCategory.Lost, "#757575"),
        ];
        db.PipelineStages.AddRange(defaults.Select((s, i) => new PipelineStage
        {
            ObjectKey = key, Key = s.Key, Label = s.Label, Probability = s.Probability, Category = s.Category, Color = s.Color, SortOrder = i,
        }));
    }

    // ---------- Fields ----------

    private static async Task<Results<Created<FieldDto>, NotFound, ValidationProblem>> CreateField(
        string key, FieldRequest req, CrmDbContext db, CancellationToken ct)
    {
        if (!await db.ObjectDefinitions.AnyAsync(o => o.Key == key, ct)) return TypedResults.NotFound();
        var fieldKey = req.Key.Trim().ToLowerInvariant();
        if (!MetadataService.KeyPattern().IsMatch(fieldKey))
            return Errors.Validation("key", "Use 2-40 lowercase letters, digits or underscores, starting with a letter.");
        if (await db.FieldDefinitions.AnyAsync(f => f.ObjectKey == key && f.Key == fieldKey, ct))
            return Errors.Validation("key", "A field with this key already exists on this object.");
        if (await ValidateFieldAsync(req, db, ct) is { } problem) return problem;

        var field = new FieldDefinition { ObjectKey = key, Key = fieldKey, Type = req.Type };
        Apply(field, req);
        db.FieldDefinitions.Add(field);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/admin/objects/{key}/fields/{fieldKey}", ToDto(field));
    }

    private static async Task<Results<Ok<FieldDto>, NotFound, ValidationProblem>> UpdateField(
        string key, string fieldKey, FieldRequest req, CrmDbContext db, CancellationToken ct)
    {
        var field = await db.FieldDefinitions.SingleOrDefaultAsync(f => f.ObjectKey == key && f.Key == fieldKey, ct);
        if (field is null) return TypedResults.NotFound();
        if (req.Type != field.Type) return Errors.Validation("type", "A field's type cannot be changed. Create a new field instead.");
        if (await ValidateFieldAsync(req, db, ct) is { } problem) return problem;
        if (req.IsUnique && !field.IsUnique && await DuplicateCountAsync(key, field, db, ct) is > 0 and var duplicates)
            return Errors.Validation("isUnique", $"{duplicates} records share a value for this field. Make them unique first.");

        Apply(field, req);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(field));
    }

    /// <summary>Records whose value for the field also appears on another record of the object.</summary>
    private static async Task<int> DuplicateCountAsync(string objectKey, FieldDefinition field, CrmDbContext db, CancellationToken ct)
    {
        var json = await CustomFieldValidator.CustomFieldsOf(db, objectKey).ToListAsync(ct);
        return json.Select(j => CustomFieldValidator.UniqueKey(field, Cf.Parse(j)[field.Key]))
            .Where(v => v is not null).GroupBy(v => v).Where(g => g.Count() > 1).Sum(g => g.Count());
    }

    private static async Task<Results<NoContent, NotFound>> DeleteField(string key, string fieldKey, CrmDbContext db, CancellationToken ct)
    {
        var field = await db.FieldDefinitions.SingleOrDefaultAsync(f => f.ObjectKey == key && f.Key == fieldKey, ct);
        if (field is null) return TypedResults.NotFound();
        db.FieldDefinitions.Remove(field);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<ValidationProblem?> ValidateFieldAsync(FieldRequest req, CrmDbContext db, CancellationToken ct)
    {
        var options = (req.Options ?? []).Select(o => o.Trim()).Where(o => o.Length > 0).ToList();
        if (req.Type is FieldType.Picklist or FieldType.MultiPicklist)
        {
            if (options.Count == 0) return Errors.Validation("options", "Add at least one option.");
            if (options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count) return Errors.Validation("options", "Options must be unique.");
        }
        if (req.Type == FieldType.Lookup &&
            (req.LookupObjectKey is null || !await db.ObjectDefinitions.AnyAsync(o => o.Key == req.LookupObjectKey, ct)))
            return Errors.Validation("lookupObjectKey", "Choose an existing object to look up.");
        if (req.Min is not null && req.Max is not null && req.Min > req.Max) return Errors.Validation("min", "Min must not exceed max.");
        if (req.IsUnique && req.Type is not (FieldType.Text or FieldType.Email or FieldType.Phone or FieldType.Url))
            return Errors.Validation("isUnique", "Only text, email, phone and URL fields can be unique.");
        return null;
    }

    private static void Apply(FieldDefinition f, FieldRequest r)
    {
        f.Label = r.Label.Trim();
        f.IsRequired = r.IsRequired;
        f.Options = Cf.Serialize((r.Options ?? []).Select(o => o.Trim()).Where(o => o.Length > 0).ToArray());
        f.LookupObjectKey = f.Type == FieldType.Lookup ? r.LookupObjectKey : null;
        f.MaxLength = r.MaxLength;
        f.Min = r.Min;
        f.Max = r.Max;
        f.HelpText = r.HelpText;
        f.ShowInList = r.ShowInList;
        f.SortOrder = r.SortOrder;
        f.IsUnique = r.IsUnique;
    }

    // ---------- Stages & picklists ----------

    private static async Task<Results<Ok<ObjectDto>, NotFound, ValidationProblem, Conflict<string>>> ReplaceStages(
        string key, StagesRequest req, CrmDbContext db, CancellationToken ct)
    {
        var definition = await db.ObjectDefinitions.SingleOrDefaultAsync(o => o.Key == key, ct);
        if (definition is null) return TypedResults.NotFound();
        if (!definition.HasPipeline) return Errors.Validation("stages", "This object does not have a pipeline.");

        var stages = req.Stages;
        if (stages.Length == 0 || stages.All(s => s.Category != StageCategory.Open))
            return Errors.Validation("stages", "Add at least one open stage.");
        if (stages.Select(s => s.Key).Distinct().Count() != stages.Length) return Errors.Validation("stages", "Stage keys must be unique.");
        foreach (var s in stages)
        {
            if (!MetadataService.StageKeyPattern().IsMatch(s.Key)) return Errors.Validation("stages", $"Invalid stage key '{s.Key}'.");
            if (string.IsNullOrWhiteSpace(s.Label) || s.Label.Length > 100) return Errors.Validation("stages", "Each stage needs a label (max 100 characters).");
            if (s.Probability is < 0 or > 100) return Errors.Validation("stages", "Probability must be between 0 and 100.");
            if (!ColorPattern().IsMatch(s.Color)) return Errors.Validation("stages", "Colours must be #RRGGBB.");
        }

        var existing = await db.PipelineStages.Where(s => s.ObjectKey == key).ToListAsync(ct);
        var removed = existing.Select(s => s.Key).Except(stages.Select(s => s.Key)).ToList();
        if (removed.Count > 0)
        {
            var inUse = key == ObjectKeys.Opportunity
                ? await db.Opportunities.CountAsync(o => removed.Contains(o.Stage), ct)
                : await db.CustomRecords.CountAsync(r => r.ObjectDefinitionId == definition.Id && removed.Contains(r.Stage), ct);
            if (inUse > 0) return TypedResults.Conflict($"{inUse} records are in stages you removed ({string.Join(", ", removed)}). Move them first.");
        }

        var order = 0;
        foreach (var s in stages)
        {
            var stage = existing.FirstOrDefault(e => e.Key == s.Key);
            if (stage is null)
            {
                stage = new PipelineStage { ObjectKey = key, Key = s.Key };
                db.PipelineStages.Add(stage);
            }
            stage.Label = s.Label.Trim();
            stage.Probability = s.Probability;
            stage.Category = s.Category;
            stage.Color = s.Color;
            stage.SortOrder = order++;
        }
        db.PipelineStages.RemoveRange(existing.Where(e => removed.Contains(e.Key)));
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await LoadObjectAsync(db, key, ct));
    }

    private static async Task<Results<Ok<string[]>, ValidationProblem>> UpdatePicklist(string key, PicklistRequest req, CrmDbContext db, CancellationToken ct)
    {
        if (key is not ("account.industry" or "lead.source")) return Errors.Validation("key", "Unknown picklist.");
        var options = req.Options.Select(o => o.Trim()).Where(o => o.Length is > 0 and <= 64).Distinct().ToArray();
        var picklist = await db.Picklists.SingleOrDefaultAsync(p => p.Key == key, ct);
        if (picklist is null) db.Picklists.Add(picklist = new Picklist { Key = key });
        picklist.Options = Cf.Serialize(options);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(options);
    }

    // ---------- Mapping ----------

    private static async Task<ObjectDto> LoadObjectAsync(CrmDbContext db, string key, CancellationToken ct)
    {
        var o = await db.ObjectDefinitions.AsNoTracking().SingleAsync(x => x.Key == key, ct);
        var fields = await db.FieldDefinitions.AsNoTracking().Where(f => f.ObjectKey == key).OrderBy(f => f.SortOrder).ToListAsync(ct);
        var stages = await db.PipelineStages.AsNoTracking().Where(s => s.ObjectKey == key).OrderBy(s => s.SortOrder).ToListAsync(ct);
        return ToDto(o, fields, stages);
    }

    private static ObjectDto ToDto(ObjectDefinition o, IEnumerable<FieldDefinition> fields, IEnumerable<PipelineStage> stages) => new(
        o.Key, o.Label, o.PluralLabel, o.Icon, o.Description, o.NameLabel, o.IsStandard, o.IsActive, o.HasPipeline, o.SortOrder,
        fields.Select(ToDto).ToList(),
        stages.Select(s => new StageDto(s.Key, s.Label, s.Probability, s.Category, s.Color)).ToList());

    private static FieldDto ToDto(FieldDefinition f) => new(
        f.Id, f.Key, f.Label, f.Type, f.IsRequired, Cf.ParseStrings(f.Options), f.LookupObjectKey,
        f.MaxLength, f.Min, f.Max, f.HelpText, f.ShowInList, f.SortOrder, f.IsUnique);
}
