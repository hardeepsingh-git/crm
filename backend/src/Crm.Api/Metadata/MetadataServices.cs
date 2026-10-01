using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Metadata;

/// <summary>Per-request cache of the tenant's configuration (objects, fields, stages, picklists).</summary>
public sealed partial class MetadataService(CrmDbContext db)
{
    private List<ObjectDefinition>? _objects;
    private readonly Dictionary<string, List<FieldDefinition>> _fields = [];
    private readonly Dictionary<string, List<PipelineStage>> _stages = [];
    private readonly Dictionary<string, string[]> _picklists = [];

    [GeneratedRegex("^[a-z][a-z0-9_]{1,39}$")]
    public static partial Regex KeyPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    public static partial Regex StageKeyPattern();

    public async Task<List<ObjectDefinition>> ObjectsAsync(CancellationToken ct = default) =>
        _objects ??= await db.ObjectDefinitions.AsNoTracking().OrderBy(o => o.SortOrder).ThenBy(o => o.Label).ToListAsync(ct);

    public async Task<ObjectDefinition?> ObjectAsync(string key, CancellationToken ct = default) =>
        (await ObjectsAsync(ct)).FirstOrDefault(o => o.Key == key);

    public async Task<List<FieldDefinition>> FieldsAsync(string objectKey, CancellationToken ct = default)
    {
        if (_fields.TryGetValue(objectKey, out var cached)) return cached;
        var fields = await db.FieldDefinitions.AsNoTracking().Where(f => f.ObjectKey == objectKey)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Label).ToListAsync(ct);
        return _fields[objectKey] = fields;
    }

    public async Task<List<PipelineStage>> StagesAsync(string objectKey, CancellationToken ct = default)
    {
        if (_stages.TryGetValue(objectKey, out var cached)) return cached;
        var stages = await db.PipelineStages.AsNoTracking().Where(s => s.ObjectKey == objectKey).OrderBy(s => s.SortOrder).ToListAsync(ct);
        return _stages[objectKey] = stages;
    }

    public async Task<string[]> PicklistAsync(string key, CancellationToken ct = default)
    {
        if (_picklists.TryGetValue(key, out var cached)) return cached;
        var json = await db.Picklists.AsNoTracking().Where(p => p.Key == key).Select(p => p.Options).FirstOrDefaultAsync(ct);
        return _picklists[key] = Cf.ParseStrings(json);
    }

    /// <summary>Validates a value against a configured picklist; an empty picklist accepts anything, and an unchanged
    /// <paramref name="current"/> value stays valid even if it was later removed from the picklist.</summary>
    public async Task<string?> CheckPicklistAsync(string key, string? value, string? current, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(value) || value == current) return null;
        var options = await PicklistAsync(key, ct);
        return options.Length == 0 || options.Contains(value) ? null : $"Must be one of: {string.Join(", ", options)}.";
    }
}

public record CustomFieldResult(string Json, Dictionary<string, string[]> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Validates and normalises custom field values against the tenant's field definitions.</summary>
public sealed class CustomFieldValidator(MetadataService metadata, CrmDbContext db)
{
    /// <param name="input">Values to set; null values clear a field. Keys not present keep their existing value.</param>
    public async Task<CustomFieldResult> ValidateAsync(string objectKey, JsonObject? input, string? existingJson, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var fields = (await metadata.FieldsAsync(objectKey, ct)).ToDictionary(f => f.Key);
        var result = Cf.Parse(existingJson);
        foreach (var stale in result.Select(p => p.Key).Where(k => !fields.ContainsKey(k)).ToList()) result.Remove(stale);

        foreach (var (key, value) in input ?? [])
        {
            if (!fields.TryGetValue(key, out var field))
            {
                errors[$"customFields.{key}"] = ["Unknown field."];
                continue;
            }
            if (value is null || (value is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s)))
            {
                result.Remove(key);
                continue;
            }
            var (normalized, error) = await NormalizeAsync(field, value, ct);
            if (error is not null) errors[$"customFields.{key}"] = [error];
            else result[key] = normalized;
        }

        foreach (var field in fields.Values.Where(f => f.IsRequired && result[f.Key] is null))
            errors.TryAdd($"customFields.{field.Key}", [$"{field.Label} is required."]);

        return new CustomFieldResult(result.ToJsonString(), errors);
    }

    private async Task<(JsonNode? Value, string? Error)> NormalizeAsync(FieldDefinition f, JsonNode value, CancellationToken ct)
    {
        string? Text() => value is JsonValue jv && jv.TryGetValue<string>(out var s) ? s.Trim() : null;
        decimal? Number()
        {
            if (value is not JsonValue jv) return null;
            if (jv.TryGetValue<decimal>(out var d)) return d;
            return jv.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d) ? d : null;
        }

        switch (f.Type)
        {
            case FieldType.Text or FieldType.TextArea or FieldType.Phone:
            {
                var text = Text();
                if (text is null) return (null, "Must be text.");
                var max = f.MaxLength ?? (f.Type == FieldType.TextArea ? 4000 : 255);
                return text.Length > max ? (null, $"Must be at most {max} characters.") : (JsonValue.Create(text), null);
            }
            case FieldType.Email:
            {
                var text = Text();
                return text is not null && new EmailAddressAttribute().IsValid(text) && text.Length <= 256
                    ? (JsonValue.Create(text), null) : (null, "Must be a valid email address.");
            }
            case FieldType.Url:
            {
                var text = Text();
                return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    ? (JsonValue.Create(text), null) : (null, "Must be an http(s) URL.");
            }
            case FieldType.Number or FieldType.Currency:
            {
                var number = Number();
                if (number is null) return (null, "Must be a number.");
                if (f.Min is { } min && number < min) return (null, $"Must be at least {min}.");
                if (f.Max is { } max && number > max) return (null, $"Must be at most {max}.");
                return (JsonValue.Create(CustomFieldQuery.Normalize(number.Value)), null);
            }
            case FieldType.Boolean:
                return value is JsonValue b && b.TryGetValue<bool>(out var flag) ? (JsonValue.Create(flag), null) : (null, "Must be true or false.");
            case FieldType.Date:
                return DateOnly.TryParseExact(Text(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? (JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), null)
                    : (null, "Must be a date (yyyy-MM-dd).");
            case FieldType.DateTime:
                return DateTimeOffset.TryParse(Text(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
                    ? (JsonValue.Create(dt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)), null)
                    : (null, "Must be an ISO date-time.");
            case FieldType.Picklist:
            {
                var options = Cf.ParseStrings(f.Options);
                var text = Text();
                return text is not null && options.Contains(text) ? (JsonValue.Create(text), null) : (null, $"Must be one of: {string.Join(", ", options)}.");
            }
            case FieldType.MultiPicklist:
            {
                var options = Cf.ParseStrings(f.Options);
                if (value is not JsonArray array) return (null, "Must be a list.");
                var picked = array.Select(Cf.AsText).ToList();
                if (picked.Any(p => p is null || !options.Contains(p))) return (null, $"Values must be from: {string.Join(", ", options)}.");
                return (new JsonArray(picked.Distinct().Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()), null);
            }
            case FieldType.Lookup:
            {
                if (!Guid.TryParse(Text(), out var id)) return (null, "Must be a record id.");
                return await LookupExistsAsync(f.LookupObjectKey!, id, ct)
                    ? (JsonValue.Create(id.ToString()), null)
                    : (null, "Referenced record does not exist.");
            }
            default:
                return (null, "Unsupported field type.");
        }
    }

    private readonly Dictionary<(string ObjectKey, Guid Id), bool> _lookups = [];

    private async Task<bool> LookupExistsAsync(string objectKey, Guid id, CancellationToken ct)
    {
        if (_lookups.TryGetValue((objectKey, id), out var known)) return known;
        return _lookups[(objectKey, id)] = objectKey switch
        {
            ObjectKeys.Account => await db.Accounts.AnyAsync(x => x.Id == id, ct),
            ObjectKeys.Contact => await db.Contacts.AnyAsync(x => x.Id == id, ct),
            ObjectKeys.Lead => await db.Leads.AnyAsync(x => x.Id == id, ct),
            ObjectKeys.Opportunity => await db.Opportunities.AnyAsync(x => x.Id == id, ct),
            ObjectKeys.Activity => await db.Activities.AnyAsync(x => x.Id == id, ct),
            _ => await db.CustomRecords.AnyAsync(x => x.Id == id && x.ObjectDefinition!.Key == objectKey, ct),
        };
    }
}

public static class CustomFieldQuery
{
    /// <summary>Removes trailing zeros so 5, 5.0 and 5.00 serialise identically.</summary>
    public static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;

    /// <summary>Applies <c>cf.&lt;key&gt;=value</c> query-string filters (exact match) to any entity with custom fields.</summary>
    public static IQueryable<T> WhereCustomFields<T>(this IQueryable<T> query, IQueryCollection queryString, CrmDbContext db)
        where T : class, IHasCustomFields
    {
        foreach (var (name, values) in queryString)
        {
            if (!name.StartsWith("cf.", StringComparison.Ordinal) || name.Length <= 3) continue;
            var key = name[3..];
            var raw = values.ToString();
            JsonNode node = bool.TryParse(raw, out var b) ? JsonValue.Create(b)
                : decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(Normalize(d))
                : JsonValue.Create(raw);
            var json = new JsonObject { [key] = node }.ToJsonString();
            if (db.Database.IsNpgsql())
                query = query.Where(e => EF.Functions.JsonContains(e.CustomFields, json));
            else
            {
                var fragment = json[1..^1];
                query = query.Where(e => e.CustomFields.Contains(fragment));
            }
        }
        return query;
    }
}
