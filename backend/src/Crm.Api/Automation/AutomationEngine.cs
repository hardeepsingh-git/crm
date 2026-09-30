using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Automation;

/// <param name="Original">Entity populated with pre-change values (updates only).</param>
public record RecordChange(OwnedEntity Entity, object? Original, bool IsNew, bool StageChanged);

/// <summary>Reflection-based view of a record as a flat field map: standard properties (camelCase) plus <c>cf.&lt;key&gt;</c> custom fields.</summary>
public static class RecordFields
{
    private static readonly HashSet<string> NotSettable =
        ["id", "tenantId", "ownerId", "createdAt", "updatedAt", "customFields", "stage", "closedAt", "objectDefinitionId",
         "convertedAt", "convertedAccountId", "convertedContactId", "convertedOpportunityId", "status"];

    public static IEnumerable<PropertyInfo> ScalarProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && IsScalar(p.PropertyType) && p.Name != nameof(IHasCustomFields.CustomFields));

    public static IEnumerable<string> SettableFields(Type type) =>
        ScalarProperties(type).Where(p => p.CanWrite).Select(p => Camel(p.Name)).Where(n => !NotSettable.Contains(n));

    public static Dictionary<string, object?> Read(object entity)
    {
        var fields = ScalarProperties(entity.GetType()).ToDictionary(p => Camel(p.Name), p => p.GetValue(entity));
        if (entity is IHasCustomFields cf)
            foreach (var (key, value) in Cf.Parse(cf.CustomFields))
                fields[$"cf.{key}"] = Cf.AsText(value);
        return fields;
    }

    public static bool TrySet(object entity, string field, string? value, FieldDefinition? definition)
    {
        if (field.StartsWith("cf.", StringComparison.Ordinal))
        {
            if (entity is not IHasCustomFields cf || definition is null) return false;
            var json = Cf.Parse(cf.CustomFields);
            json[definition.Key] = value is null ? null : definition.Type switch
            {
                FieldType.Number or FieldType.Currency when decimal.TryParse(value, CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
                FieldType.Boolean when bool.TryParse(value, out var b) => JsonValue.Create(b),
                _ => JsonValue.Create(value),
            };
            cf.CustomFields = json.ToJsonString();
            return true;
        }
        if (!SettableFields(entity.GetType()).Contains(field)) return false;
        var prop = entity.GetType().GetProperty(char.ToUpperInvariant(field[0]) + field[1..])!;
        var target = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        try
        {
            object? converted = value is null ? null
                : target.IsEnum ? Enum.Parse(target, value, ignoreCase: true)
                : Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
            prop.SetValue(entity, converted);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }

    public static string Format(object? value) => value switch
    {
        null => "",
        decimal d => d.ToString("0.##", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static bool IsScalar(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid)
               || t == typeof(DateTime) || t == typeof(DateOnly);
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>Runs tenant-defined rules after records are created or updated. Invoked by <see cref="CrmDbContext"/> after a successful save.</summary>
public sealed partial class AutomationEngine(TimeProvider clock, ILogger<AutomationEngine> logger)
{
    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_.]+)\s*\}\}")]
    private static partial Regex Placeholder();

    /// <returns>True when actions produced changes that need saving.</returns>
    public async Task<bool> RunAsync(CrmDbContext db, IReadOnlyList<RecordChange> changes, CancellationToken ct)
    {
        var definitionIds = changes.Select(c => c.Entity).OfType<CustomRecord>().Select(r => r.ObjectDefinitionId).Distinct().ToList();
        var customKeys = await db.ObjectDefinitions.Where(d => definitionIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Key, ct);
        var keyed = changes.Select(c => (Change: c, Key: CrmDbContext.EntityTypeKey(c.Entity, customKeys))).ToList();
        var keys = keyed.Select(k => k.Key).Distinct().ToList();

        var rules = await db.AutomationRules.Where(r => r.IsActive && keys.Contains(r.ObjectKey)).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        if (rules.Count == 0) return false;

        var fieldDefs = await db.FieldDefinitions.Where(f => keys.Contains(f.ObjectKey)).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var changed = false;

        foreach (var (change, key) in keyed)
        {
            var current = RecordFields.Read(change.Entity);
            var before = change.Original is null ? null : RecordFields.Read(change.Original);
            foreach (var rule in rules.Where(r => r.ObjectKey == key && Matches(r.Trigger, change)))
            {
                try
                {
                    var conditions = JsonSerializer.Deserialize<List<AutomationCondition>>(rule.Conditions, AutomationJson.Options) ?? [];
                    if (!conditions.All(c => Evaluate(c, current, before))) continue;
                    var actions = JsonSerializer.Deserialize<List<AutomationAction>>(rule.Actions, AutomationJson.Options) ?? [];
                    foreach (var action in actions)
                        await ExecuteAsync(db, rule, action, change.Entity, key, current, fieldDefs, now, ct);
                    rule.RunCount++;
                    rule.LastRunAt = now;
                    changed = true;
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                {
                    logger.LogWarning(ex, "Automation rule {Rule} ({RuleId}) failed", rule.Name, rule.Id);
                }
            }
        }
        return changed;
    }

    private static bool Matches(AutomationTrigger trigger, RecordChange change) => trigger switch
    {
        AutomationTrigger.Created => change.IsNew,
        AutomationTrigger.Updated => !change.IsNew,
        AutomationTrigger.StageChanged => change.StageChanged,
        _ => false,
    };

    public static bool Evaluate(AutomationCondition c, IReadOnlyDictionary<string, object?> current, IReadOnlyDictionary<string, object?>? before)
    {
        var actual = RecordFields.Format(current.GetValueOrDefault(c.Field));
        var expected = c.Value ?? "";
        var numeric = decimal.TryParse(actual, CultureInfo.InvariantCulture, out var a) & decimal.TryParse(expected, CultureInfo.InvariantCulture, out var e);
        return c.Operator switch
        {
            ConditionOperator.Equals => numeric ? a == e : string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.NotEquals => numeric ? a != e : !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.GreaterThan => numeric && a > e,
            ConditionOperator.LessThan => numeric && a < e,
            ConditionOperator.Contains => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.IsEmpty => actual.Length == 0,
            ConditionOperator.IsNotEmpty => actual.Length > 0,
            ConditionOperator.Changed => before is not null && RecordFields.Format(before.GetValueOrDefault(c.Field)) != actual,
            _ => false,
        };
    }

    private async Task ExecuteAsync(
        CrmDbContext db, AutomationRule rule, AutomationAction action, OwnedEntity entity, string objectKey,
        IReadOnlyDictionary<string, object?> fields, List<FieldDefinition> fieldDefs, DateTime now, CancellationToken ct)
    {
        switch (action.Type)
        {
            case AutomationActionType.CreateTask:
            {
                var task = new Activity
                {
                    Type = ActivityType.Task,
                    Subject = Truncate(Render(action.Subject ?? rule.Name, fields), 200),
                    Description = $"Created by automation rule \"{rule.Name}\".",
                    DueAt = now.Date.AddDays(action.DueInDays ?? 1).AddHours(17),
                    Priority = action.Priority ?? ActivityPriority.Normal,
                    OwnerId = await ResolveUserAsync(db, action.AssignTo, entity.OwnerId, ct),
                };
                Link(task, entity);
                db.Activities.Add(task);
                break;
            }
            case AutomationActionType.Notify:
                db.Notifications.Add(new Notification
                {
                    UserId = await ResolveUserAsync(db, action.AssignTo, entity.OwnerId, ct),
                    Title = Truncate(rule.Name, 200),
                    Message = Truncate(Render(action.Message ?? "{{name}} matched this rule.", fields), 1000),
                    Link = LinkFor(entity, objectKey),
                    CreatedAt = now,
                });
                break;
            case AutomationActionType.SetField when action.Field is not null:
            {
                var definition = action.Field.StartsWith("cf.", StringComparison.Ordinal)
                    ? fieldDefs.FirstOrDefault(f => f.ObjectKey == objectKey && f.Key == action.Field[3..])
                    : null;
                if (!RecordFields.TrySet(entity, action.Field, action.Value is null ? null : Render(action.Value, fields), definition))
                    throw new InvalidOperationException($"Cannot set field '{action.Field}'.");
                break;
            }
        }
    }

    public static string Render(string template, IReadOnlyDictionary<string, object?> fields) =>
        Placeholder().Replace(template, m => RecordFields.Format(fields.GetValueOrDefault(m.Groups[1].Value)));

    private static async Task<Guid> ResolveUserAsync(CrmDbContext db, string? assignTo, Guid owner, CancellationToken ct)
    {
        if (!Guid.TryParse(assignTo, out var userId)) return owner;
        var tenantId = db.CurrentTenantId;
        return await db.Users.AnyAsync(u => u.Id == userId && u.TenantId == tenantId && u.IsActive, ct) ? userId : owner;
    }

    private static void Link(Activity task, OwnedEntity entity)
    {
        switch (entity)
        {
            case Account a: task.AccountId = a.Id; break;
            case Contact c: task.ContactId = c.Id; task.AccountId = c.AccountId; break;
            case Lead l: task.LeadId = l.Id; break;
            case Opportunity o: task.OpportunityId = o.Id; task.AccountId = o.AccountId; task.ContactId = o.ContactId; break;
            case Activity a:
                task.AccountId = a.AccountId; task.ContactId = a.ContactId; task.LeadId = a.LeadId;
                task.OpportunityId = a.OpportunityId; task.CustomRecordId = a.CustomRecordId;
                break;
            case CustomRecord r: task.CustomRecordId = r.Id; break;
        }
    }

    public static string LinkFor(OwnedEntity entity, string objectKey) => entity switch
    {
        Account => $"/accounts/{entity.Id}",
        Contact => $"/contacts/{entity.Id}",
        Lead => $"/leads/{entity.Id}",
        Opportunity => $"/opportunities/{entity.Id}",
        Activity => "/activities",
        _ => $"/o/{objectKey}/{entity.Id}",
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

public static class AutomationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
