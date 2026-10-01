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

namespace Crm.Api.Features.Opportunities;

public record OpportunityListItem(
    Guid Id, string Name, Guid AccountId, string AccountName, Guid? ContactId, string? ContactName,
    decimal Amount, string Stage, int Probability, DateOnly ExpectedCloseDate,
    Guid OwnerId, string OwnerName, DateTime CreatedAt, JsonObject CustomFields);

public record OpportunityDetail(
    Guid Id, string Name, Guid AccountId, string AccountName, Guid? ContactId, string? ContactName,
    decimal Amount, string Stage, int Probability, DateOnly ExpectedCloseDate,
    DateTime? ClosedAt, string? LossReason, string? Description,
    Guid OwnerId, string OwnerName, DateTime CreatedAt, DateTime UpdatedAt, JsonObject CustomFields);

public record OpportunityRequest(
    [Required, MaxLength(200)] string Name,
    Guid AccountId,
    Guid? ContactId,
    [Range(0, 1_000_000_000_000)] decimal Amount,
    [Required, MaxLength(64)] string Stage,
    [Range(0, 100)] int? Probability,
    DateOnly ExpectedCloseDate,
    [MaxLength(500)] string? LossReason,
    [MaxLength(4000)] string? Description,
    Guid? OwnerId,
    JsonObject? CustomFields = null);

public record StageChangeRequest([Required, MaxLength(64)] string Stage, [MaxLength(500)] string? LossReason);

public record OpportunityQuery(
    string? Search, string? Stage, Guid? AccountId, Guid? ContactId, Guid? OwnerId, bool? Open, bool Mine = false,
    int Page = 1, int PageSize = 20, string? SortBy = null, string? SortDir = null);

public static class OpportunityEndpoints
{
    private const string R = ObjectKeys.Opportunity;

    public static RouteGroupBuilder MapOpportunities(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/opportunities").WithTags("Opportunities");
        g.MapGet("/", List).RequirePermission(R, Read);
        g.MapGet("/pipeline", Pipeline).RequirePermission(R, Read);
        g.MapGet("/{id:guid}", Get).RequirePermission(R, Read);
        g.MapPost("/", CreateAsync).RequirePermission(R, Create);
        g.MapPut("/{id:guid}", UpdateAsync).RequirePermission(R, Update);
        g.MapPatch("/{id:guid}/stage", ChangeStage).RequirePermission(R, Update);
        g.MapDelete("/{id:guid}", DeleteAsync).RequirePermission(R, Delete);
        return api;
    }

    private static async Task<Ok<PagedResult<OpportunityListItem>>> List(
        [AsParameters] OpportunityQuery q, HttpRequest http, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var query = p.Filter(db.Opportunities.AsNoTracking(), R).WhereCustomFields(http.Query, db);
        var search = QueryableExtensions.NormalizeSearch(q.Search);
        if (search is not null)
            query = query.Where(o => o.Name.ToLower().Contains(search) || o.Account!.Name.ToLower().Contains(search));
        if (!string.IsNullOrWhiteSpace(q.Stage)) query = query.Where(o => o.Stage == q.Stage);
        if (q.AccountId is not null) query = query.Where(o => o.AccountId == q.AccountId);
        if (q.ContactId is not null) query = query.Where(o => o.ContactId == q.ContactId);
        if (q.Open == true) query = query.Where(o => o.ClosedAt == null);
        if (q.Open == false) query = query.Where(o => o.ClosedAt != null);
        if (q.Mine) query = query.Where(o => o.OwnerId == p.UserId);
        else if (q.OwnerId is not null) query = query.Where(o => o.OwnerId == q.OwnerId);

        var desc = QueryableExtensions.IsDescending(q.SortDir);
        query = q.SortBy?.ToLowerInvariant() switch
        {
            "name" => query.OrderByDir(o => o.Name, desc),
            "amount" => query.OrderByDir(o => o.Amount, desc),
            "stage" => query.OrderByDir(o => o.Stage, desc),
            "account" => query.OrderByDir(o => o.Account!.Name, desc),
            "createdat" => query.OrderByDir(o => o.CreatedAt, desc),
            _ => query.OrderByDir(o => o.ExpectedCloseDate, desc),
        };

        return TypedResults.Ok(await ToListItems(query).ToPagedAsync(q.Page, q.PageSize, ct));
    }

    /// <summary>All open deals plus deals closed in the last 30 days, for the Kanban board.</summary>
    private static async Task<Ok<List<OpportunityListItem>>> Pipeline(
        Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct, bool mine = false)
    {
        var p = await access.GetAsync(ct);
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        var query = p.Filter(db.Opportunities.AsNoTracking(), R).Where(o => o.ClosedAt == null || o.ClosedAt >= since);
        if (mine) query = query.Where(o => o.OwnerId == p.UserId);
        return TypedResults.Ok(await ToListItems(query.OrderBy(o => o.ExpectedCloseDate)).Take(500).ToListAsync(ct));
    }

    private static async Task<Results<Ok<OpportunityDetail>, NotFound>> Get(Guid id, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var detail = await Project(p.Filter(db.Opportunities.AsNoTracking(), R).Where(o => o.Id == id)).SingleOrDefaultAsync(ct);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    private static async Task<Results<Created<OpportunityDetail>, ValidationProblem>> CreateAsync(
        OpportunityRequest req, Access access, CrmDbContext db, MetadataService meta, CustomFieldValidator validator, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var opp = new Opportunity { OwnerId = req.OwnerId ?? p.UserId };
        if (await ValidateAsync(req, opp, p.UserId, p, db, meta, validator, clock.GetUtcNow().UtcDateTime, ct) is { } problem) return problem;

        db.Opportunities.Add(opp);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/opportunities/{opp.Id}", await Project(db.Opportunities.AsNoTracking().Where(o => o.Id == opp.Id)).SingleAsync(ct));
    }

    private static async Task<Results<Ok<OpportunityDetail>, NotFound, ForbidHttpResult, ValidationProblem>> UpdateAsync(
        Guid id, OpportunityRequest req, Access access, CrmDbContext db, MetadataService meta, CustomFieldValidator validator, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var opp = await db.Opportunities.FindAsync([id], ct);
        if (opp is null || !p.CanAccess(R, Read, opp.OwnerId)) return TypedResults.NotFound();
        if (!p.CanAccess(R, Update, opp.OwnerId)) return TypedResults.Forbid();
        if (await ValidateAsync(req, opp, opp.OwnerId, p, db, meta, validator, clock.GetUtcNow().UtcDateTime, ct) is { } problem) return problem;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await Project(db.Opportunities.AsNoTracking().Where(o => o.Id == id)).SingleAsync(ct));
    }

    private static async Task<Results<Ok<OpportunityDetail>, NotFound, ForbidHttpResult, ValidationProblem>> ChangeStage(
        Guid id, StageChangeRequest req, Access access, CrmDbContext db, MetadataService meta, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var opp = await db.Opportunities.FindAsync([id], ct);
        if (opp is null || !p.CanAccess(R, Read, opp.OwnerId)) return TypedResults.NotFound();
        if (!p.CanAccess(R, Update, opp.OwnerId)) return TypedResults.Forbid();
        var stage = (await meta.StagesAsync(R, ct)).FirstOrDefault(s => s.Key == req.Stage);
        if (stage is null) return Errors.Validation("stage", "Unknown stage.");

        opp.MoveToStage(stage, clock.GetUtcNow().UtcDateTime);
        if (stage.Category == StageCategory.Lost) opp.LossReason = req.LossReason;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await Project(db.Opportunities.AsNoTracking().Where(o => o.Id == id)).SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult>> DeleteAsync(Guid id, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var opp = await db.Opportunities.FindAsync([id], ct);
        if (opp is null || !p.CanAccess(R, Read, opp.OwnerId)) return TypedResults.NotFound();
        if (!p.CanAccess(R, Delete, opp.OwnerId)) return TypedResults.Forbid();
        db.Opportunities.Remove(opp);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<ValidationProblem?> ValidateAsync(OpportunityRequest r, Opportunity o, Guid currentOwner, EffectivePermissions p,
        CrmDbContext db, MetadataService meta, CustomFieldValidator validator, DateTime now, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!await db.Accounts.AnyAsync(a => a.Id == r.AccountId, ct)) errors["accountId"] = ["Account does not exist."];
        if (r.ContactId is not null && !await db.Contacts.AnyAsync(c => c.Id == r.ContactId, ct)) errors["contactId"] = ["Contact does not exist."];
        if (await Ownership.ValidateAsync(r.OwnerId, currentOwner, R, p, db, ct) is { } ownerError) errors["ownerId"] = [ownerError];
        var stage = (await meta.StagesAsync(R, ct)).FirstOrDefault(s => s.Key == r.Stage);
        if (stage is null) errors["stage"] = ["Unknown stage."];
        var cf = await validator.ValidateAsync(R, r.CustomFields, o.CustomFields, o.Id, ct);
        foreach (var (k, v) in cf.Errors) errors[k] = v;
        if (errors.Count > 0) return Errors.Validation(errors);

        o.Name = r.Name.Trim();
        o.AccountId = r.AccountId;
        o.ContactId = r.ContactId;
        o.Amount = r.Amount;
        o.ExpectedCloseDate = r.ExpectedCloseDate;
        o.Description = r.Description;
        o.MoveToStage(stage!, now, r.Probability);
        if (stage!.Category == StageCategory.Lost) o.LossReason = r.LossReason;
        o.OwnerId = r.OwnerId ?? o.OwnerId;
        o.CustomFields = cf.Json;
        return null;
    }

    private static IQueryable<OpportunityListItem> ToListItems(IQueryable<Opportunity> q) => q.Select(o => new OpportunityListItem(
        o.Id, o.Name, o.AccountId, o.Account!.Name, o.ContactId,
        o.Contact != null ? o.Contact.FirstName + " " + o.Contact.LastName : null,
        o.Amount, o.Stage, o.Probability, o.ExpectedCloseDate,
        o.OwnerId, o.Owner!.FirstName + " " + o.Owner.LastName, o.CreatedAt, Cf.Parse(o.CustomFields)));

    private static IQueryable<OpportunityDetail> Project(IQueryable<Opportunity> q) => q.Select(o => new OpportunityDetail(
        o.Id, o.Name, o.AccountId, o.Account!.Name, o.ContactId,
        o.Contact != null ? o.Contact.FirstName + " " + o.Contact.LastName : null,
        o.Amount, o.Stage, o.Probability, o.ExpectedCloseDate, o.ClosedAt, o.LossReason, o.Description,
        o.OwnerId, o.Owner!.FirstName + " " + o.Owner.LastName, o.CreatedAt, o.UpdatedAt, Cf.Parse(o.CustomFields)));
}
