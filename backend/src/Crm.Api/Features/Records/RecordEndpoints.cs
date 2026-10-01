using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Metadata;
using Crm.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using static Crm.Api.Domain.PermissionActions;

namespace Crm.Api.Features.Records;

public record RecordItem(
    Guid Id, string ObjectKey, string Name, string Stage, DateTime? ClosedAt,
    Guid OwnerId, string OwnerName, DateTime CreatedAt, DateTime UpdatedAt, JsonObject CustomFields);

public record RecordRequest(
    [Required, MaxLength(200)] string Name,
    [MaxLength(64)] string? Stage,
    Guid? OwnerId,
    JsonObject? CustomFields = null);

public record RecordStageRequest([Required, MaxLength(64)] string Stage);

public record RecordQuery(
    string? Search, string? Stage, Guid? OwnerId, bool? Open, bool Mine = false,
    int Page = 1, int PageSize = 20, string? SortBy = null, string? SortDir = null);

/// <summary>CRUD for tenant-defined objects. The permission resource is the object key.</summary>
public static class RecordEndpoints
{
    private static string ObjectKey(HttpContext ctx) => ctx.GetRouteValue("objectKey")?.ToString() ?? "";

    public static RouteGroupBuilder MapRecords(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/records/{objectKey}").WithTags("Custom records");
        g.MapGet("/", List).RequirePermission(ObjectKey, Read);
        g.MapGet("/board", Board).RequirePermission(ObjectKey, Read);
        g.MapGet("/{id:guid}", Get).RequirePermission(ObjectKey, Read);
        g.MapPost("/", CreateAsync).RequirePermission(ObjectKey, Create);
        g.MapPut("/{id:guid}", UpdateAsync).RequirePermission(ObjectKey, Update);
        g.MapPatch("/{id:guid}/stage", ChangeStage).RequirePermission(ObjectKey, Update);
        g.MapDelete("/{id:guid}", DeleteAsync).RequirePermission(ObjectKey, Delete);
        return api;
    }

    private static async Task<ObjectDefinition?> ResolveAsync(string objectKey, MetadataService meta, CancellationToken ct) =>
        await meta.ObjectAsync(objectKey, ct) is { IsStandard: false, IsActive: true } definition ? definition : null;

    private static async Task<Results<Ok<PagedResult<RecordItem>>, NotFound>> List(
        string objectKey, [AsParameters] RecordQuery q, HttpRequest http, Access access, CrmDbContext db, MetadataService meta, CancellationToken ct)
    {
        if (await ResolveAsync(objectKey, meta, ct) is not { } definition) return TypedResults.NotFound();
        var p = await access.GetAsync(ct);
        var query = p.Filter(db.CustomRecords.AsNoTracking().Where(r => r.ObjectDefinitionId == definition.Id), objectKey)
            .WhereCustomFields(http.Query, db);
        var search = QueryableExtensions.NormalizeSearch(q.Search);
        if (search is not null) query = query.Where(r => r.Name.ToLower().Contains(search));
        if (!string.IsNullOrWhiteSpace(q.Stage)) query = query.Where(r => r.Stage == q.Stage);
        if (q.Open == true) query = query.Where(r => r.ClosedAt == null);
        if (q.Open == false) query = query.Where(r => r.ClosedAt != null);
        if (q.Mine) query = query.Where(r => r.OwnerId == p.UserId);
        else if (q.OwnerId is not null) query = query.Where(r => r.OwnerId == q.OwnerId);

        var desc = QueryableExtensions.IsDescending(q.SortDir);
        query = q.SortBy?.ToLowerInvariant() switch
        {
            "name" => query.OrderByDir(r => r.Name, desc),
            "stage" => query.OrderByDir(r => r.Stage, desc),
            "updatedat" => query.OrderByDir(r => r.UpdatedAt, desc),
            _ => query.OrderByDir(r => r.CreatedAt, q.SortDir is null || desc),
        };
        return TypedResults.Ok(await Project(query, objectKey).ToPagedAsync(q.Page, q.PageSize, ct));
    }

    private static async Task<Results<Ok<List<RecordItem>>, NotFound>> Board(
        string objectKey, HttpRequest http, Access access, CrmDbContext db, MetadataService meta, TimeProvider clock, CancellationToken ct, bool mine = false)
    {
        if (await ResolveAsync(objectKey, meta, ct) is not { HasPipeline: true } definition) return TypedResults.NotFound();
        var p = await access.GetAsync(ct);
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        var query = p.Filter(db.CustomRecords.AsNoTracking().Where(r => r.ObjectDefinitionId == definition.Id), objectKey)
            .WhereCustomFields(http.Query, db)
            .Where(r => r.ClosedAt == null || r.ClosedAt >= since);
        if (mine) query = query.Where(r => r.OwnerId == p.UserId);
        return TypedResults.Ok(await Project(query.OrderByDescending(r => r.UpdatedAt), objectKey).Take(500).ToListAsync(ct));
    }

    private static async Task<Results<Ok<RecordItem>, NotFound>> Get(
        string objectKey, Guid id, Access access, CrmDbContext db, MetadataService meta, CancellationToken ct)
    {
        if (await ResolveAsync(objectKey, meta, ct) is not { } definition) return TypedResults.NotFound();
        var p = await access.GetAsync(ct);
        var item = await Project(p.Filter(db.CustomRecords.AsNoTracking(), objectKey)
            .Where(r => r.Id == id && r.ObjectDefinitionId == definition.Id), objectKey).SingleOrDefaultAsync(ct);
        return item is null ? TypedResults.NotFound() : TypedResults.Ok(item);
    }

    private static async Task<Results<Created<RecordItem>, NotFound, ValidationProblem>> CreateAsync(
        string objectKey, RecordRequest req, Access access, CrmDbContext db, MetadataService meta, CustomFieldValidator validator,
        TimeProvider clock, CancellationToken ct)
    {
        if (await ResolveAsync(objectKey, meta, ct) is not { } definition) return TypedResults.NotFound();
        var p = await access.GetAsync(ct);
        var record = new CustomRecord { ObjectDefinitionId = definition.Id, OwnerId = req.OwnerId ?? p.UserId };
        if (await ValidateAsync(definition, req, record, p.UserId, p, db, meta, validator, clock.GetUtcNow().UtcDateTime, ct) is { } problem)
            return problem;

        db.CustomRecords.Add(record);
        await db.SaveChangesAsync(ct);
        var item = await Project(db.CustomRecords.AsNoTracking().Where(r => r.Id == record.Id), objectKey).SingleAsync(ct);
        return TypedResults.Created($"/api/records/{objectKey}/{record.Id}", item);
    }

    private static async Task<Results<Ok<RecordItem>, NotFound, ForbidHttpResult, ValidationProblem>> UpdateAsync(
        string objectKey, Guid id, RecordRequest req, Access access, CrmDbContext db, MetadataService meta, CustomFieldValidator validator,
        TimeProvider clock, CancellationToken ct)
    {
        if (await ResolveAsync(objectKey, meta, ct) is not { } definition) return TypedResults.NotFound();
        var p = await access.GetAsync(ct);
        var record = await db.CustomRecords.SingleOrDefaultAsync(r => r.Id == id && r.ObjectDefinitionId == definition.Id, ct);
        if (record is null || !p.CanAccess(objectKey, Read, record.OwnerId)) return TypedResults.NotFound();
        if (!p.CanAccess(objectKey, Update, record.OwnerId)) return TypedResults.Forbid();
        if (await ValidateAsync(definition, req, record, record.OwnerId, p, db, meta, validator, clock.GetUtcNow().UtcDateTime, ct) is { } problem)
            return problem;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await Project(db.CustomRecords.AsNoTracking().Where(r => r.Id == id), objectKey).SingleAsync(ct));
    }

    private static async Task<Results<Ok<RecordItem>, NotFound, ForbidHttpResult, ValidationProblem>> ChangeStage(
        string objectKey, Guid id, RecordStageRequest req, Access access, CrmDbContext db, MetadataService meta, TimeProvider clock, CancellationToken ct)
    {
        if (await ResolveAsync(objectKey, meta, ct) is not { HasPipeline: true } definition) return TypedResults.NotFound();
        var p = await access.GetAsync(ct);
        var record = await db.CustomRecords.SingleOrDefaultAsync(r => r.Id == id && r.ObjectDefinitionId == definition.Id, ct);
        if (record is null || !p.CanAccess(objectKey, Read, record.OwnerId)) return TypedResults.NotFound();
        if (!p.CanAccess(objectKey, Update, record.OwnerId)) return TypedResults.Forbid();
        var stage = (await meta.StagesAsync(objectKey, ct)).FirstOrDefault(s => s.Key == req.Stage);
        if (stage is null) return Errors.Validation("stage", "Unknown stage.");

        ApplyStage(record, stage, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await Project(db.CustomRecords.AsNoTracking().Where(r => r.Id == id), objectKey).SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult>> DeleteAsync(
        string objectKey, Guid id, Access access, CrmDbContext db, MetadataService meta, CancellationToken ct)
    {
        if (await ResolveAsync(objectKey, meta, ct) is not { } definition) return TypedResults.NotFound();
        var p = await access.GetAsync(ct);
        var record = await db.CustomRecords.SingleOrDefaultAsync(r => r.Id == id && r.ObjectDefinitionId == definition.Id, ct);
        if (record is null || !p.CanAccess(objectKey, Read, record.OwnerId)) return TypedResults.NotFound();
        if (!p.CanAccess(objectKey, Delete, record.OwnerId)) return TypedResults.Forbid();
        db.CustomRecords.Remove(record);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static void ApplyStage(CustomRecord record, PipelineStage stage, DateTime now)
    {
        if (record.Stage == stage.Key) return;
        record.Stage = stage.Key;
        record.ClosedAt = stage.Category == StageCategory.Open ? null : now;
    }

    private static async Task<ValidationProblem?> ValidateAsync(ObjectDefinition definition, RecordRequest r, CustomRecord record, Guid currentOwner,
        EffectivePermissions p, CrmDbContext db, MetadataService meta, CustomFieldValidator validator, DateTime now, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (await Ownership.ValidateAsync(r.OwnerId, currentOwner, definition.Key, p, db, ct) is { } ownerError) errors["ownerId"] = [ownerError];

        PipelineStage? stage = null;
        if (definition.HasPipeline)
        {
            var stages = await meta.StagesAsync(definition.Key, ct);
            var key = r.Stage ?? (record.Stage.Length > 0 ? record.Stage : stages.FirstOrDefault()?.Key);
            stage = stages.FirstOrDefault(s => s.Key == key);
            if (stage is null) errors["stage"] = ["Unknown stage."];
        }

        var cf = await validator.ValidateAsync(definition.Key, r.CustomFields, record.CustomFields, record.Id, ct);
        foreach (var (k, v) in cf.Errors) errors[k] = v;
        if (errors.Count > 0) return Errors.Validation(errors);

        record.Name = r.Name.Trim();
        if (stage is not null) ApplyStage(record, stage, now);
        record.OwnerId = r.OwnerId ?? record.OwnerId;
        record.CustomFields = cf.Json;
        return null;
    }

    private static IQueryable<RecordItem> Project(IQueryable<CustomRecord> q, string objectKey) => q.Select(r => new RecordItem(
        r.Id, objectKey, r.Name, r.Stage, r.ClosedAt,
        r.OwnerId, r.Owner!.FirstName + " " + r.Owner.LastName, r.CreatedAt, r.UpdatedAt, Cf.Parse(r.CustomFields)));
}
