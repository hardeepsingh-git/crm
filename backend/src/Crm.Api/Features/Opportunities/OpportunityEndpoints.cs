using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Opportunities;

public record OpportunityListItem(
    Guid Id, string Name, Guid AccountId, string AccountName, Guid? ContactId, string? ContactName,
    decimal Amount, OpportunityStage Stage, int Probability, DateOnly ExpectedCloseDate,
    Guid OwnerId, string OwnerName, DateTime CreatedAt);

public record OpportunityDetail(
    Guid Id, string Name, Guid AccountId, string AccountName, Guid? ContactId, string? ContactName,
    decimal Amount, OpportunityStage Stage, int Probability, DateOnly ExpectedCloseDate,
    DateTime? ClosedAt, string? LossReason, string? Description,
    Guid OwnerId, string OwnerName, DateTime CreatedAt, DateTime UpdatedAt);

public record OpportunityRequest(
    [Required, MaxLength(200)] string Name,
    Guid AccountId,
    Guid? ContactId,
    [Range(0, 1_000_000_000_000)] decimal Amount,
    OpportunityStage Stage,
    [Range(0, 100)] int? Probability,
    DateOnly ExpectedCloseDate,
    [MaxLength(500)] string? LossReason,
    [MaxLength(4000)] string? Description,
    Guid? OwnerId);

public record StageChangeRequest(OpportunityStage Stage, [MaxLength(500)] string? LossReason);

public record OpportunityQuery(
    string? Search, OpportunityStage? Stage, Guid? AccountId, Guid? ContactId, Guid? OwnerId, bool? Open, bool Mine = false,
    int Page = 1, int PageSize = 20, string? SortBy = null, string? SortDir = null);

public static class OpportunityEndpoints
{
    public static RouteGroupBuilder MapOpportunities(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/opportunities").WithTags("Opportunities");
        g.MapGet("/", List);
        g.MapGet("/pipeline", Pipeline);
        g.MapGet("/{id:guid}", Get);
        g.MapPost("/", Create);
        g.MapPut("/{id:guid}", Update);
        g.MapPatch("/{id:guid}/stage", ChangeStage);
        g.MapDelete("/{id:guid}", Delete).RequireAuthorization(Policies.ManagerOrAdmin);
        return api;
    }

    private static IQueryable<Opportunity> OpenOnly(this IQueryable<Opportunity> q) =>
        q.Where(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost);

    private static async Task<Ok<PagedResult<OpportunityListItem>>> List(
        [AsParameters] OpportunityQuery q, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        var query = db.Opportunities.AsNoTracking();
        var search = QueryableExtensions.NormalizeSearch(q.Search);
        if (search is not null)
            query = query.Where(o => o.Name.ToLower().Contains(search) || o.Account!.Name.ToLower().Contains(search));
        if (q.Stage is not null) query = query.Where(o => o.Stage == q.Stage);
        if (q.AccountId is not null) query = query.Where(o => o.AccountId == q.AccountId);
        if (q.ContactId is not null) query = query.Where(o => o.ContactId == q.ContactId);
        if (q.Open == true) query = query.OpenOnly();
        if (q.Open == false) query = query.Where(o => o.Stage == OpportunityStage.ClosedWon || o.Stage == OpportunityStage.ClosedLost);
        if (q.Mine) query = query.Where(o => o.OwnerId == user.GetUserId());
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

        var result = await ToListItems(query).ToPagedAsync(q.Page, q.PageSize, ct);
        return TypedResults.Ok(result);
    }

    /// <summary>All open deals plus deals closed in the last 30 days, for the Kanban board.</summary>
    private static async Task<Ok<List<OpportunityListItem>>> Pipeline(
        ClaimsPrincipal user, CrmDbContext db, TimeProvider clock, CancellationToken ct, bool mine = false)
    {
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        var query = db.Opportunities.AsNoTracking()
            .Where(o => (o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost) || o.ClosedAt >= since);
        if (mine) query = query.Where(o => o.OwnerId == user.GetUserId());
        var items = await ToListItems(query.OrderBy(o => o.ExpectedCloseDate)).Take(500).ToListAsync(ct);
        return TypedResults.Ok(items);
    }

    private static async Task<Results<Ok<OpportunityDetail>, NotFound>> Get(Guid id, CrmDbContext db, CancellationToken ct)
    {
        var detail = await Project(db.Opportunities.AsNoTracking().Where(o => o.Id == id)).SingleOrDefaultAsync(ct);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    private static async Task<Results<Created<OpportunityDetail>, ValidationProblem>> Create(
        OpportunityRequest req, ClaimsPrincipal user, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = user.GetUserId();
        if (await ValidateAsync(req, me, user, db, ct) is { } problem) return problem;

        var opp = new Opportunity { OwnerId = req.OwnerId ?? me, Stage = req.Stage, Probability = StageDefaults.Probability(req.Stage) };
        Apply(opp, req, clock.GetUtcNow().UtcDateTime);
        if (opp.IsClosed) opp.ClosedAt = clock.GetUtcNow().UtcDateTime;
        db.Opportunities.Add(opp);
        await db.SaveChangesAsync(ct);

        var detail = await Project(db.Opportunities.AsNoTracking().Where(o => o.Id == opp.Id)).SingleAsync(ct);
        return TypedResults.Created($"/api/opportunities/{opp.Id}", detail);
    }

    private static async Task<Results<Ok<OpportunityDetail>, NotFound, ForbidHttpResult, ValidationProblem>> Update(
        Guid id, OpportunityRequest req, ClaimsPrincipal user, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var opp = await db.Opportunities.FindAsync([id], ct);
        if (opp is null) return TypedResults.NotFound();
        if (!user.CanModify(opp.OwnerId)) return TypedResults.Forbid();
        if (await ValidateAsync(req, opp.OwnerId, user, db, ct) is { } problem) return problem;

        Apply(opp, req, clock.GetUtcNow().UtcDateTime);
        opp.OwnerId = req.OwnerId ?? opp.OwnerId;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await Project(db.Opportunities.AsNoTracking().Where(o => o.Id == id)).SingleAsync(ct));
    }

    private static async Task<Results<Ok<OpportunityDetail>, NotFound, ForbidHttpResult>> ChangeStage(
        Guid id, StageChangeRequest req, ClaimsPrincipal user, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var opp = await db.Opportunities.FindAsync([id], ct);
        if (opp is null) return TypedResults.NotFound();
        if (!user.CanModify(opp.OwnerId)) return TypedResults.Forbid();

        opp.MoveToStage(req.Stage, clock.GetUtcNow().UtcDateTime);
        if (req.Stage == OpportunityStage.ClosedLost) opp.LossReason = req.LossReason;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await Project(db.Opportunities.AsNoTracking().Where(o => o.Id == id)).SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound>> Delete(Guid id, CrmDbContext db, CancellationToken ct)
    {
        var opp = await db.Opportunities.FindAsync([id], ct);
        if (opp is null) return TypedResults.NotFound();
        db.Opportunities.Remove(opp);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<ValidationProblem?> ValidateAsync(OpportunityRequest req, Guid currentOwner, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        if (!await db.Accounts.AnyAsync(a => a.Id == req.AccountId, ct))
            return Errors.Validation("accountId", "Account does not exist.");
        if (req.ContactId is not null && !await db.Contacts.AnyAsync(c => c.Id == req.ContactId, ct))
            return Errors.Validation("contactId", "Contact does not exist.");
        if (await Ownership.ValidateAsync(req.OwnerId, currentOwner, user, db, ct) is { } err)
            return Errors.Validation("ownerId", err);
        return null;
    }

    private static void Apply(Opportunity o, OpportunityRequest r, DateTime now)
    {
        o.Name = r.Name.Trim();
        o.AccountId = r.AccountId;
        o.ContactId = r.ContactId;
        o.Amount = r.Amount;
        o.ExpectedCloseDate = r.ExpectedCloseDate;
        o.Description = r.Description;
        o.MoveToStage(r.Stage, now, r.Probability);
        if (r.Stage == OpportunityStage.ClosedLost) o.LossReason = r.LossReason;
    }

    private static IQueryable<OpportunityListItem> ToListItems(IQueryable<Opportunity> q) => q.Select(o => new OpportunityListItem(
        o.Id, o.Name, o.AccountId, o.Account!.Name, o.ContactId,
        o.Contact != null ? o.Contact.FirstName + " " + o.Contact.LastName : null,
        o.Amount, o.Stage, o.Probability, o.ExpectedCloseDate,
        o.OwnerId, o.Owner!.FirstName + " " + o.Owner.LastName, o.CreatedAt));

    private static IQueryable<OpportunityDetail> Project(IQueryable<Opportunity> q) => q.Select(o => new OpportunityDetail(
        o.Id, o.Name, o.AccountId, o.Account!.Name, o.ContactId,
        o.Contact != null ? o.Contact.FirstName + " " + o.Contact.LastName : null,
        o.Amount, o.Stage, o.Probability, o.ExpectedCloseDate, o.ClosedAt, o.LossReason, o.Description,
        o.OwnerId, o.Owner!.FirstName + " " + o.Owner.LastName, o.CreatedAt, o.UpdatedAt));
}
