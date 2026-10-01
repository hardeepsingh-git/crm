using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Crm.Api.Automation;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Metadata;
using Crm.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Export;

/// <summary>
/// CSV export of any object's list, honouring the caller's read scope and the same filters as the list screens. Linked
/// records appear by name (by id if the caller can't read them), headers use field labels so the file can be re-imported,
/// and every export is written to the audit log.
/// </summary>
public static partial class ExportEndpoints
{
    private static readonly HashSet<string> Reserved =
        new(["search", "mine", "ownerId", "stage", "open", "page", "pageSize", "sortBy", "sortDir", "tenantId", "customFields"],
            StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Hidden = ["Id", "TenantId", "OwnerId", "ObjectDefinitionId", "CreatedAt", "UpdatedAt"];

    /// <summary>Foreign keys on standard objects and the object they point to ("*" = any custom record).</summary>
    private static readonly Dictionary<string, string> References = new()
    {
        ["AccountId"] = ObjectKeys.Account, ["ConvertedAccountId"] = ObjectKeys.Account,
        ["ContactId"] = ObjectKeys.Contact, ["ConvertedContactId"] = ObjectKeys.Contact,
        ["LeadId"] = ObjectKeys.Lead,
        ["OpportunityId"] = ObjectKeys.Opportunity, ["ConvertedOpportunityId"] = ObjectKeys.Opportunity,
        ["CustomRecordId"] = "*",
    };

    public static RouteGroupBuilder MapExport(this RouteGroupBuilder api)
    {
        api.MapGet("/export/{objectKey}", Export).WithTags("Export")
            .RequirePermission(ctx => ctx.GetRouteValue("objectKey")?.ToString() ?? "", PermissionActions.Read);
        return api;
    }

    private static async Task<IResult> Export(
        string objectKey, HttpRequest http, Access access, CrmDbContext db, MetadataService meta, IConfiguration config, TimeProvider clock,
        CancellationToken ct)
    {
        if (await meta.ObjectAsync(objectKey, ct) is not { IsActive: true } definition) return TypedResults.NotFound();
        var p = await access.GetAsync(ct);
        var q = http.Query;
        var now = clock.GetUtcNow().UtcDateTime;

        IQueryable<OwnedEntity> query = objectKey switch
        {
            ObjectKeys.Account => Prepare(db.Accounts, q, p, objectKey, db, s => x => x.Name.ToLower().Contains(s)),
            ObjectKeys.Contact => Prepare(db.Contacts, q, p, objectKey, db,
                s => x => (x.FirstName + " " + x.LastName).ToLower().Contains(s) || (x.Email != null && x.Email.ToLower().Contains(s))),
            ObjectKeys.Lead => Prepare(db.Leads, q, p, objectKey, db,
                s => x => (x.FirstName + " " + x.LastName).ToLower().Contains(s) || x.Company.ToLower().Contains(s)),
            ObjectKeys.Opportunity => Staged(Prepare(db.Opportunities, q, p, objectKey, db, s => x => x.Name.ToLower().Contains(s)), q),
            ObjectKeys.Activity => ActivityStatus(Prepare(db.Activities, q, p, objectKey, db, s => x => x.Subject.ToLower().Contains(s)), q, now),
            _ => Staged(Prepare(db.CustomRecords.Where(r => r.ObjectDefinitionId == definition.Id), q, p, objectKey, db,
                s => x => x.Name.ToLower().Contains(s)), q),
        };

        var max = config.GetValue("Export:MaxRows", 10000);
        var total = await query.CountAsync(ct);
        if (total > max)
            return Errors.Validation("filters", $"{total} records match, and an export can hold at most {max}. Narrow the filters and try again.");
        var rows = await query.OrderByDescending(x => x.CreatedAt).ToListAsync(ct);

        var columns = await ColumnsAsync(objectKey, definition, rows, p, db, meta, ct);
        var csv = new StringBuilder();
        csv.Append(string.Join(',', columns.Select(c => Cell(c.Header, isText: true)))).Append("\r\n");
        foreach (var row in rows)
            csv.Append(string.Join(',', columns.Select(c => { var (text, isText) = c.Value(row); return Cell(text, isText); }))).Append("\r\n");

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = objectKey, EntityId = Guid.Empty,
            EntityName = $"{rows.Count} {(rows.Count == 1 ? definition.Label : definition.PluralLabel).ToLowerInvariant()}",
            Action = AuditAction.Exported, UserId = p.UserId, UserName = db.TenantContext.UserName, Timestamp = now,
            Changes = new JsonObject { ["filters"] = new JsonObject(q.Where(kv => kv.Key is not ("page" or "pageSize" or "sortBy" or "sortDir"))
                .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value.ToString())))) }.ToJsonString(),
        });
        await db.SaveChangesAsync(ct);

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        var fileName = $"{FileSafe().Replace(definition.PluralLabel.ToLowerInvariant(), "-").Trim('-')}-{now:yyyyMMdd}.csv";
        return TypedResults.File(bytes, "text/csv; charset=utf-8", fileName);
    }

    // ---------- Filters ----------

    private static IQueryable<T> Prepare<T>(
        IQueryable<T> source, IQueryCollection q, EffectivePermissions p, string objectKey, CrmDbContext db,
        Func<string, Expression<Func<T, bool>>> search) where T : OwnedEntity
    {
        var query = p.Filter(source.AsNoTracking(), objectKey).WhereCustomFields(q, db);
        query = WhereProperties(query, q);
        if (QueryableExtensions.NormalizeSearch(q["search"]) is { } term) query = query.Where(search(term));
        if (bool.TryParse(q["mine"], out var mine) && mine) query = query.Where(x => x.OwnerId == p.UserId);
        else if (Guid.TryParse(q["ownerId"], out var owner)) query = query.Where(x => x.OwnerId == owner);
        return query;
    }

    private static IQueryable<T> Staged<T>(IQueryable<T> query, IQueryCollection q) where T : OwnedEntity, IStaged
    {
        var stage = q["stage"].ToString();
        if (stage.Length > 0) query = query.Where(x => x.Stage == stage);
        if (bool.TryParse(q["open"], out var open)) query = open ? query.Where(x => x.ClosedAt == null) : query.Where(x => x.ClosedAt != null);
        return query;
    }

    private static IQueryable<Activity> ActivityStatus(IQueryable<Activity> query, IQueryCollection q, DateTime now) => q["status"].ToString() switch
    {
        "Open" => query.Where(a => a.CompletedAt == null),
        "Completed" => query.Where(a => a.CompletedAt != null),
        "Overdue" => query.Where(a => a.CompletedAt == null && a.DueAt != null && a.DueAt < now),
        _ => query,
    };

    /// <summary>Equality filters on standard properties named in the query string, e.g. <c>?status=Qualified&amp;industry=Retail</c>.</summary>
    private static IQueryable<T> WhereProperties<T>(IQueryable<T> query, IQueryCollection q)
    {
        foreach (var (name, values) in q)
        {
            if (Reserved.Contains(name) || name.StartsWith("cf.", StringComparison.Ordinal)) continue;
            var property = typeof(T).GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null || !RecordFields.ScalarProperties(typeof(T)).Contains(property)) continue;
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? value;
            try
            {
                var raw = values.ToString();
                value = type.IsEnum ? Enum.Parse(type, raw, ignoreCase: true)
                    : type == typeof(Guid) ? Guid.Parse(raw)
                    : Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidCastException or OverflowException)
            {
                continue;
            }
            var x = Expression.Parameter(typeof(T), "x");
            var body = Expression.Equal(Expression.Property(x, property), Expression.Constant(value, property.PropertyType));
            query = query.Where(Expression.Lambda<Func<T, bool>>(body, x));
        }
        return query;
    }

    // ---------- Columns ----------

    private sealed record Column(string Header, Func<OwnedEntity, (string Text, bool IsText)> Value);

    private static async Task<List<Column>> ColumnsAsync(
        string objectKey, ObjectDefinition definition, List<OwnedEntity> rows, EffectivePermissions p, CrmDbContext db, MetadataService meta,
        CancellationToken ct)
    {
        var type = RecordStore.EntityType(objectKey);
        var fields = await meta.FieldsAsync(objectKey, ct);
        var stages = (await meta.StagesAsync(objectKey, ct)).ToDictionary(s => s.Key, s => s.Label);
        var objects = await meta.ObjectsAsync(ct);

        // Names of every linked record, resolved in one query per object.
        var wanted = new Dictionary<string, HashSet<Guid>>();
        void Want(string key, Guid? id)
        {
            if (id is not { } value || value == Guid.Empty) return;
            if (!wanted.TryGetValue(key, out var set)) wanted[key] = set = [];
            set.Add(value);
        }
        var properties = RecordFields.ScalarProperties(type).Where(pr => !Hidden.Contains(pr.Name)).ToList();
        foreach (var row in rows)
        {
            foreach (var pr in properties.Where(pr => References.ContainsKey(pr.Name))) Want(References[pr.Name], pr.GetValue(row) as Guid?);
            var values = Cf.Parse(row.CustomFields);
            foreach (var f in fields.Where(f => f.Type == FieldType.Lookup && f.LookupObjectKey is not null))
                Want(f.LookupObjectKey!, Guid.TryParse(Cf.AsText(values[f.Key]), out var id) ? id : null);
        }
        var names = new Dictionary<Guid, string>();
        foreach (var (key, ids) in wanted)
            foreach (var (id, name) in await NamesAsync(db, key, ids, p, ct)) names[id] = name;
        var owners = await db.Users.Where(u => u.TenantId == p.TenantId).Select(u => new { u.Id, Name = u.FirstName + " " + u.LastName })
            .ToDictionaryAsync(u => u.Id, u => u.Name, ct);

        string Name(Guid? id) => id is { } g && g != Guid.Empty ? names.GetValueOrDefault(g, g.ToString()) : "";
        string Label(string key) => objects.FirstOrDefault(o => o.Key == key)?.Label ?? RecordFields.Humanize(key);

        var columns = new List<Column> { new("Id", r => (r.Id.ToString(), false)) };
        foreach (var pr in properties)
        {
            if (!definition.HasPipeline && pr.Name is nameof(IStaged.Stage) or nameof(IStaged.ClosedAt)) continue;
            var header = pr.Name == nameof(CustomRecord.Name) && type == typeof(CustomRecord) ? definition.NameLabel
                : References.TryGetValue(pr.Name, out var target)
                    ? (target == "*" ? "Related record" : pr.Name.StartsWith("Converted", StringComparison.Ordinal) ? $"Converted {Label(target).ToLowerInvariant()}" : Label(target))
                    : RecordFields.Humanize(char.ToLowerInvariant(pr.Name[0]) + pr.Name[1..]);
            columns.Add(new(header, r =>
            {
                var value = pr.GetValue(r);
                if (References.ContainsKey(pr.Name)) return (Name(value as Guid?), true);
                if (pr.Name == nameof(IStaged.Stage) && value is string stage) return (stages.GetValueOrDefault(stage, stage), true);
                return Format(value);
            }));
        }
        foreach (var f in fields)
        {
            var header = columns.Any(c => c.Header == f.Label) ? $"{f.Label} (custom)" : f.Label;
            columns.Add(new(header, r =>
            {
                var node = Cf.Parse(r.CustomFields)[f.Key];
                return f.Type switch
                {
                    FieldType.Lookup => (Name(Guid.TryParse(Cf.AsText(node), out var id) ? id : null), true),
                    FieldType.MultiPicklist => (node is JsonArray a ? string.Join("; ", a.Select(Cf.AsText)) : Cf.AsText(node) ?? "", true),
                    FieldType.Boolean => (node is JsonValue v && v.TryGetValue<bool>(out var b) ? (b ? "Yes" : "No") : "", false),
                    FieldType.Number or FieldType.Currency => (Cf.AsText(node) ?? "", false),
                    FieldType.Date or FieldType.DateTime => (Cf.AsText(node) ?? "", false),
                    _ => (Cf.AsText(node) ?? "", true),
                };
            }));
        }
        columns.Add(new("Owner", r => (owners.GetValueOrDefault(r.OwnerId, ""), true)));
        columns.Add(new("Created at", r => Format(r.CreatedAt)));
        columns.Add(new("Updated at", r => Format(r.UpdatedAt)));
        return columns;
    }

    /// <summary>Linked records the caller can read, by name; others are left to show as ids.</summary>
    private static async Task<List<(Guid Id, string Name)>> NamesAsync(
        CrmDbContext db, string key, HashSet<Guid> ids, EffectivePermissions p, CancellationToken ct)
    {
        var list = ids.ToList();
        var found = key switch
        {
            ObjectKeys.Account => await db.Accounts.Where(x => list.Contains(x.Id)).Select(x => new { x.Id, x.Name, x.OwnerId, Key = key }).ToListAsync(ct),
            ObjectKeys.Contact => await db.Contacts.Where(x => list.Contains(x.Id))
                .Select(x => new { x.Id, Name = x.FirstName + " " + x.LastName, x.OwnerId, Key = key }).ToListAsync(ct),
            ObjectKeys.Lead => await db.Leads.Where(x => list.Contains(x.Id))
                .Select(x => new { x.Id, Name = x.FirstName + " " + x.LastName, x.OwnerId, Key = key }).ToListAsync(ct),
            ObjectKeys.Opportunity => await db.Opportunities.Where(x => list.Contains(x.Id)).Select(x => new { x.Id, x.Name, x.OwnerId, Key = key }).ToListAsync(ct),
            ObjectKeys.Activity => await db.Activities.Where(x => list.Contains(x.Id)).Select(x => new { x.Id, Name = x.Subject, x.OwnerId, Key = key }).ToListAsync(ct),
            _ => await db.CustomRecords.Where(x => list.Contains(x.Id)).Select(x => new { x.Id, x.Name, x.OwnerId, Key = x.ObjectDefinition!.Key }).ToListAsync(ct),
        };
        return found.Where(x => p.CanAccess(x.Key, PermissionActions.Read, x.OwnerId)).Select(x => (x.Id, x.Name)).ToList();
    }

    private static (string Text, bool IsText) Format(object? value) => value switch
    {
        null => ("", false),
        string s => (s, true),
        bool b => (b ? "Yes" : "No", false),
        DateTime dt => (DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), false),
        DateOnly d => (d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), false),
        Enum e => (e.ToString(), false),
        IFormattable f => (f.ToString(null, CultureInfo.InvariantCulture), false),
        _ => (value.ToString() ?? "", true),
    };

    /// <summary>
    /// RFC 4180 cell. Text that a spreadsheet would run as a formula (starting with = + - @, tab or CR) is prefixed with an
    /// apostrophe (CSV injection); numbers and dates are left as they are.
    /// </summary>
    public static string Cell(string value, bool isText)
    {
        if (isText && value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        var quote = value.IndexOfAny([',', '"', '\r', '\n']) >= 0 || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])));
        return quote ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex FileSafe();
}
