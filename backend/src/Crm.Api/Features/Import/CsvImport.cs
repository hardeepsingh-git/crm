using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Metadata;
using Crm.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Import;

public record CsvRow(int Line, string[] Cells);

/// <summary>RFC 4180 CSV: quoted fields (with "" escapes and embedded line breaks), BOM, and comma, semicolon or tab delimiters.</summary>
public static class Csv
{
    public static char DetectDelimiter(string text)
    {
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0 };
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && (c == '\n' || c == '\r')) break;
            else if (!quoted && counts.ContainsKey(c)) counts[c]++;
        }
        return counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key == ',' ? 0 : 1).First().Key;
    }

    /// <summary>Parses all rows, skipping blank lines. Each row records the line it starts on (1-based).</summary>
    public static List<CsvRow> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        var delimiter = DetectDelimiter(text);
        var rows = new List<CsvRow>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var line = 1;
        var rowStart = 1;

        void EndCell()
        {
            cells.Add(cell.ToString());
            cell.Clear();
        }

        void EndRow()
        {
            EndCell();
            if (cells.Any(c => c.Trim().Length > 0)) rows.Add(new CsvRow(rowStart, [.. cells]));
            cells.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else
                {
                    if (c == '\n') line++;
                    cell.Append(c);
                }
            }
            else if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == delimiter) EndCell();
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndRow();
                line++;
                rowStart = line;
            }
            else cell.Append(c);
        }
        if (quoted) throw new FormatException($"A quoted value starting on line {rowStart} is never closed.");
        if (cell.Length > 0 || cells.Count > 0) EndRow();
        return rows;
    }
}

public record ImportAnalyzeRequest([Required] string Csv);

public record ImportTarget(string Key, string Label, string Type, bool Required, string? Help);

public record ImportAnalysis(
    IReadOnlyList<string> Headers, int RowCount, IReadOnlyList<string[]> Sample,
    Dictionary<string, string?> SuggestedMapping, IReadOnlyList<ImportTarget> Targets);

public record ImportRequest([Required] string Csv, [Required] Dictionary<string, string?> Mapping, bool DryRun = true, bool RunAutomation = false);

public record ImportRowError(int Row, IReadOnlyList<string> Messages);

public record ImportResult(bool DryRun, int Total, int Created, int ErrorCount, IReadOnlyList<ImportRowError> Errors);

/// <summary>
/// Bulk-creates records of a custom object from CSV. Every row goes through the same validation as the forms, and the
/// import is all-or-nothing: nothing is saved unless every row is valid.
/// </summary>
public static class ImportEndpoints
{
    public const string Name = "name";
    public const string Stage = "stage";
    public const string Owner = "owner";
    private const int MaxReportedErrors = 200;

    private static string ObjectKey(HttpContext ctx) => ctx.GetRouteValue("objectKey")?.ToString() ?? "";

    public static RouteGroupBuilder MapImport(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/import/{objectKey}").WithTags("Import");
        g.MapPost("/analyze", Analyze).RequirePermission(ObjectKey, PermissionActions.Create);
        g.MapPost("/", Run).RequirePermission(ObjectKey, PermissionActions.Create);
        return api;
    }

    private static async Task<ObjectDefinition?> ResolveAsync(string objectKey, MetadataService meta, CancellationToken ct) =>
        await meta.ObjectAsync(objectKey, ct) is { IsStandard: false, IsActive: true } definition ? definition : null;

    private static ValidationProblem? CheckSize(string csv, IConfiguration config, out List<CsvRow> rows)
    {
        rows = [];
        var maxBytes = config.GetValue("Import:MaxFileSizeMb", 5) * 1024 * 1024;
        if (Encoding.UTF8.GetByteCount(csv) > maxBytes) return Errors.Validation("csv", $"The file is larger than {maxBytes / 1024 / 1024} MB. Split it into smaller files.");
        try { rows = Csv.Parse(csv); }
        catch (FormatException ex) { return Errors.Validation("csv", ex.Message); }
        var maxRows = config.GetValue("Import:MaxRows", 5000);
        if (rows.Count < 2) return Errors.Validation("csv", "The file needs a header row and at least one data row.");
        if (rows.Count - 1 > maxRows) return Errors.Validation("csv", $"At most {maxRows} rows can be imported at once.");
        var headers = rows[0].Cells.Select(h => h.Trim()).ToList();
        if (headers.Any(h => h.Length == 0)) return Errors.Validation("csv", "Every column needs a header.");
        if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Count) return Errors.Validation("csv", "Column headers must be unique.");
        return null;
    }

    private static async Task<List<ImportTarget>> TargetsAsync(ObjectDefinition definition, MetadataService meta, CancellationToken ct)
    {
        var targets = new List<ImportTarget> { new(Name, definition.NameLabel, "Text", true, null) };
        if (definition.HasPipeline) targets.Add(new(Stage, "Stage", "Stage", false, "Stage name or key; defaults to the first stage."));
        targets.Add(new(Owner, "Owner", "Email", false, "Owner's email; defaults to you."));
        foreach (var f in await meta.FieldsAsync(definition.Key, ct))
            targets.Add(new($"cf.{f.Key}", f.Label, f.Type.ToString(), f.IsRequired, f.Type switch
            {
                FieldType.Lookup => "The related record's name (or ID).",
                FieldType.MultiPicklist => "Separate values with ; or |.",
                FieldType.Date => "yyyy-mm-dd or dd/mm/yyyy.",
                FieldType.Boolean => "Yes/No, True/False or 1/0.",
                _ => null,
            }));
        return targets;
    }

    private static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static async Task<Results<Ok<ImportAnalysis>, NotFound, ValidationProblem>> Analyze(
        string objectKey, ImportAnalyzeRequest req, MetadataService meta, IConfiguration config, CancellationToken ct)
    {
        if (await ResolveAsync(objectKey, meta, ct) is not { } definition) return TypedResults.NotFound();
        if (CheckSize(req.Csv, config, out var rows) is { } problem) return problem;

        var targets = await TargetsAsync(definition, meta, ct);
        var headers = rows[0].Cells.Select(h => h.Trim()).ToList();
        var used = new HashSet<string>();
        var mapping = new Dictionary<string, string?>();
        foreach (var header in headers)
        {
            var h = Normalize(header);
            var match = targets.FirstOrDefault(t => !used.Contains(t.Key) &&
                (Normalize(t.Label) == h || Normalize(t.Key.Replace("cf.", "")) == h ||
                 (t.Key == Name && h is "name") || (t.Key == Stage && h is "status") || (t.Key == Owner && h is "owneremail" or "assignedto")));
            if (match is not null) used.Add(match.Key);
            mapping[header] = match?.Key;
        }
        return TypedResults.Ok(new ImportAnalysis(headers, rows.Count - 1, rows.Skip(1).Take(5).Select(r => r.Cells).ToList(), mapping, targets));
    }

    private static async Task<Results<Ok<ImportResult>, NotFound, ValidationProblem>> Run(
        string objectKey, ImportRequest req, Access access, CrmDbContext db, MetadataService meta, CustomFieldValidator validator,
        IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        if (await ResolveAsync(objectKey, meta, ct) is not { } definition) return TypedResults.NotFound();
        if (CheckSize(req.Csv, config, out var rows) is { } problem) return problem;

        var targets = (await TargetsAsync(definition, meta, ct)).ToDictionary(t => t.Key);
        var headers = rows[0].Cells.Select(h => h.Trim()).ToList();
        var columns = new Dictionary<string, int>();
        foreach (var (header, target) in req.Mapping)
        {
            if (string.IsNullOrEmpty(target)) continue;
            var index = headers.FindIndex(h => string.Equals(h, header, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return Errors.Validation("mapping", $"Column '{header}' isn't in the file.");
            if (!targets.ContainsKey(target)) return Errors.Validation("mapping", $"'{target}' isn't a field of {definition.PluralLabel}.");
            if (!columns.TryAdd(target, index)) return Errors.Validation("mapping", $"{targets[target].Label} is mapped to more than one column.");
        }
        var missing = targets.Values.Where(t => t.Required && !columns.ContainsKey(t.Key)).Select(t => t.Label).ToList();
        if (missing.Count > 0) return Errors.Validation("mapping", $"Map a column to: {string.Join(", ", missing)}.");

        var p = await access.GetAsync(ct);
        var fields = (await meta.FieldsAsync(objectKey, ct)).ToDictionary(f => f.Key);
        var stages = definition.HasPipeline ? await meta.StagesAsync(objectKey, ct) : [];
        var dataRows = rows.Skip(1).ToList();
        string Cell(CsvRow row, string target) =>
            columns.TryGetValue(target, out var i) && i < row.Cells.Length ? row.Cells[i].Trim() : "";

        var lookups = await ResolveLookupsAsync(db, fields.Values.Where(f => f.Type == FieldType.Lookup && columns.ContainsKey($"cf.{f.Key}")),
            f => dataRows.Select(r => Cell(r, $"cf.{f.Key}")), ct);
        var owners = await ResolveOwnersAsync(db, p, columns.ContainsKey(Owner) ? dataRows.Select(r => Cell(r, Owner)) : [], definition.Key, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var records = new List<CustomRecord>();
        var errors = new List<ImportRowError>();
        foreach (var row in dataRows)
        {
            var messages = new List<string>();
            var name = Cell(row, Name);
            if (name.Length == 0) messages.Add($"{definition.NameLabel} is required.");
            else if (name.Length > 200) messages.Add($"{definition.NameLabel} must be at most 200 characters.");

            PipelineStage? stage = stages.FirstOrDefault();
            if (Cell(row, Stage) is { Length: > 0 } stageText)
            {
                stage = stages.FirstOrDefault(s => string.Equals(s.Key, stageText, StringComparison.OrdinalIgnoreCase) ||
                                                   string.Equals(s.Label, stageText, StringComparison.OrdinalIgnoreCase));
                if (stage is null) messages.Add($"Stage '{stageText}' doesn't exist. Use one of: {string.Join(", ", stages.Select(s => s.Label))}.");
            }

            var ownerId = p.UserId;
            if (Cell(row, Owner) is { Length: > 0 } ownerEmail)
            {
                if (owners.TryGetValue(ownerEmail.ToLowerInvariant(), out var resolved) && resolved.Error is null) ownerId = resolved.Id!.Value;
                else messages.Add(owners.TryGetValue(ownerEmail.ToLowerInvariant(), out var r) ? r.Error! : $"No active user with email {ownerEmail}.");
            }

            var input = new JsonObject();
            foreach (var (key, field) in fields)
            {
                if (Cell(row, $"cf.{key}") is not { Length: > 0 } text) continue;
                var (value, error) = Convert(field, text, lookups);
                if (error is not null) messages.Add($"{field.Label}: {error}");
                else input[key] = value;
            }
            var cf = await validator.ValidateAsync(objectKey, input, null, ct);
            messages.AddRange(cf.Errors.Select(e => fields.TryGetValue(e.Key.Replace("customFields.", ""), out var f) && !e.Value[0].StartsWith(f.Label)
                ? $"{f.Label}: {e.Value[0]}" : e.Value[0]));

            if (messages.Count > 0)
            {
                errors.Add(new ImportRowError(row.Line, messages));
                continue;
            }
            records.Add(new CustomRecord
            {
                ObjectDefinitionId = definition.Id, Name = name, OwnerId = ownerId, CustomFields = cf.Json,
                Stage = stage?.Key ?? "", ClosedAt = stage is null || stage.Category == StageCategory.Open ? null : now,
            });
        }

        if (errors.Count > 0 || req.DryRun)
            return TypedResults.Ok(new ImportResult(req.DryRun, dataRows.Count, 0, errors.Count, errors.Take(MaxReportedErrors).ToList()));

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.SuppressAutomation = !req.RunAutomation;
        db.CustomRecords.AddRange(records);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return TypedResults.Ok(new ImportResult(false, dataRows.Count, records.Count, 0, []));
    }

    /// <summary>Converts spreadsheet text into the JSON shape the custom-field validator expects.</summary>
    private static (JsonNode? Value, string? Error) Convert(FieldDefinition f, string text, Dictionary<string, Dictionary<string, List<Guid>>> lookups)
    {
        switch (f.Type)
        {
            case FieldType.Boolean:
                return text.ToLowerInvariant() switch
                {
                    "true" or "yes" or "y" or "1" => (JsonValue.Create(true), null),
                    "false" or "no" or "n" or "0" => (JsonValue.Create(false), null),
                    _ => (null, "Use Yes or No."),
                };
            case FieldType.Number or FieldType.Currency:
                return ParseNumber(text) is { } d ? (JsonValue.Create(d), null) : (null, $"'{text}' isn't a number. Use digits with a dot for decimals, e.g. 1234.50.");
            case FieldType.Date:
                return DateOnly.TryParseExact(text, ["yyyy-MM-dd", "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? (JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), null)
                    : (null, $"'{text}' isn't a date. Use yyyy-mm-dd or dd/mm/yyyy.");
            case FieldType.Picklist:
            {
                var option = Cf.ParseStrings(f.Options).FirstOrDefault(o => string.Equals(o, text, StringComparison.OrdinalIgnoreCase));
                return (JsonValue.Create(option ?? text), null);
            }
            case FieldType.MultiPicklist:
            {
                var options = Cf.ParseStrings(f.Options);
                var picked = text.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(v => options.FirstOrDefault(o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase)) ?? v);
                return (new JsonArray(picked.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()), null);
            }
            case FieldType.Lookup:
            {
                if (Guid.TryParse(text, out var id)) return (JsonValue.Create(id.ToString()), null);
                var matches = lookups.GetValueOrDefault(f.Key)?.GetValueOrDefault(text.ToLowerInvariant()) ?? [];
                return matches.Count switch
                {
                    1 => (JsonValue.Create(matches[0].ToString()), null),
                    0 => (null, $"No record named '{text}'."),
                    _ => (null, $"'{text}' matches {matches.Count} records; use the record ID instead."),
                };
            }
            default:
                return (JsonValue.Create(text), null);
        }
    }

    /// <summary>
    /// Accepts "1234.5", "1,234.50" and "$ 95,000" (comma thousands, dot decimals). Anything ambiguous such as "1.234,50"
    /// is rejected rather than silently misread.
    /// </summary>
    public static decimal? ParseNumber(string text)
    {
        var s = new string(text.Where(c => !char.IsWhiteSpace(c) && !char.IsSymbol(c) && !char.IsLetter(c)).ToArray());
        if (s.Contains(','))
        {
            if (s.Contains('.') && s.LastIndexOf(',') > s.IndexOf('.')) return null;
            var integerPart = s.Split('.')[0].TrimStart('-');
            var groups = integerPart.Split(',');
            if (groups[0].Length is 0 or > 3 || groups.Skip(1).Any(g => g.Length != 3) || s.Count(c => c == '.') > 1) return null;
            s = s.Replace(",", "");
        }
        return decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>Per lookup field: lower-cased name -> matching record ids, loaded in one query per field.</summary>
    private static async Task<Dictionary<string, Dictionary<string, List<Guid>>>> ResolveLookupsAsync(
        CrmDbContext db, IEnumerable<FieldDefinition> lookupFields, Func<FieldDefinition, IEnumerable<string>> valuesOf, CancellationToken ct)
    {
        var result = new Dictionary<string, Dictionary<string, List<Guid>>>();
        foreach (var f in lookupFields)
        {
            var names = valuesOf(f).Where(v => v.Length > 0 && !Guid.TryParse(v, out _)).Select(v => v.ToLowerInvariant()).Distinct().ToList();
            if (names.Count == 0) continue;
            var pairs = f.LookupObjectKey switch
            {
                ObjectKeys.Account => await db.Accounts.Where(x => names.Contains(x.Name.ToLower())).Select(x => new { x.Id, Name = x.Name }).ToListAsync(ct),
                ObjectKeys.Contact => await db.Contacts.Where(x => names.Contains((x.FirstName + " " + x.LastName).ToLower()))
                    .Select(x => new { x.Id, Name = x.FirstName + " " + x.LastName }).ToListAsync(ct),
                ObjectKeys.Lead => await db.Leads.Where(x => names.Contains((x.FirstName + " " + x.LastName).ToLower()))
                    .Select(x => new { x.Id, Name = x.FirstName + " " + x.LastName }).ToListAsync(ct),
                ObjectKeys.Opportunity => await db.Opportunities.Where(x => names.Contains(x.Name.ToLower())).Select(x => new { x.Id, Name = x.Name }).ToListAsync(ct),
                _ => await db.CustomRecords.Where(x => x.ObjectDefinition!.Key == f.LookupObjectKey && names.Contains(x.Name.ToLower()))
                    .Select(x => new { x.Id, Name = x.Name }).ToListAsync(ct),
            };
            result[f.Key] = pairs.GroupBy(x => x.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());
        }
        return result;
    }

    /// <summary>Owner emails -> user id, or the reason that owner can't be assigned (same rules as the forms).</summary>
    private static async Task<Dictionary<string, (Guid? Id, string? Error)>> ResolveOwnersAsync(
        CrmDbContext db, EffectivePermissions p, IEnumerable<string> emails, string objectKey, CancellationToken ct)
    {
        var wanted = emails.Where(e => e.Length > 0).Select(e => e.ToLowerInvariant()).Distinct().ToList();
        var result = new Dictionary<string, (Guid? Id, string? Error)>();
        if (wanted.Count == 0) return result;
        var users = await db.Users.Where(u => u.TenantId == p.TenantId && u.IsActive && u.Email != null && wanted.Contains(u.Email.ToLower()))
            .Select(u => new { u.Id, Email = u.Email!.ToLower() }).ToListAsync(ct);
        foreach (var u in users)
            result[u.Email] = await Ownership.ValidateAsync(u.Id, p.UserId, objectKey, p, db, ct) is { } error ? (null, error) : (u.Id, null);
        return result;
    }
}
