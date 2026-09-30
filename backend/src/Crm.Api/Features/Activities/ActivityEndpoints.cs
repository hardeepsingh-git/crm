using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Activities;

public record ActivityItem(
    Guid Id, ActivityType Type, string Subject, string? Description, DateTime? DueAt, DateTime? CompletedAt,
    ActivityPriority Priority, Guid? AccountId, string? AccountName, Guid? ContactId, string? ContactName,
    Guid? LeadId, string? LeadName, Guid? OpportunityId, string? OpportunityName,
    Guid OwnerId, string OwnerName, DateTime CreatedAt, DateTime UpdatedAt);

public record ActivityRequest(
    ActivityType Type,
    [Required, MaxLength(200)] string Subject,
    [MaxLength(4000)] string? Description,
    DateTime? DueAt,
    ActivityPriority Priority,
    Guid? AccountId,
    Guid? ContactId,
    Guid? LeadId,
    Guid? OpportunityId,
    Guid? OwnerId);

public enum ActivityStatusFilter { Open, Completed, Overdue }

public record ActivityQuery(
    string? Search, ActivityType? Type, ActivityStatusFilter? Status,
    Guid? AccountId, Guid? ContactId, Guid? LeadId, Guid? OpportunityId, Guid? OwnerId,
    DateTime? DueFrom, DateTime? DueTo, bool Mine = false,
    int Page = 1, int PageSize = 20, string? SortBy = null, string? SortDir = null);

public static class ActivityEndpoints
{
    public static RouteGroupBuilder MapActivities(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/activities").WithTags("Activities");
        g.MapGet("/", List);
        g.MapGet("/{id:guid}", Get);
        g.MapPost("/", Create);
        g.MapPut("/{id:guid}", Update);
        g.MapPost("/{id:guid}/complete", (Guid id, ClaimsPrincipal user, CrmDbContext db, TimeProvider clock, CancellationToken ct)
            => SetCompleted(id, true, user, db, clock, ct));
        g.MapPost("/{id:guid}/reopen", (Guid id, ClaimsPrincipal user, CrmDbContext db, TimeProvider clock, CancellationToken ct)
            => SetCompleted(id, false, user, db, clock, ct));
        g.MapDelete("/{id:guid}", Delete);
        return api;
    }

    private static async Task<Ok<PagedResult<ActivityItem>>> List(
        [AsParameters] ActivityQuery q, ClaimsPrincipal user, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var query = db.Activities.AsNoTracking();
        var search = QueryableExtensions.NormalizeSearch(q.Search);
        if (search is not null) query = query.Where(a => a.Subject.ToLower().Contains(search));
        if (q.Type is not null) query = query.Where(a => a.Type == q.Type);
        query = q.Status switch
        {
            ActivityStatusFilter.Open => query.Where(a => a.CompletedAt == null),
            ActivityStatusFilter.Completed => query.Where(a => a.CompletedAt != null),
            ActivityStatusFilter.Overdue => query.Where(a => a.CompletedAt == null && a.DueAt < now),
            _ => query,
        };
        if (q.AccountId is not null) query = query.Where(a => a.AccountId == q.AccountId);
        if (q.ContactId is not null) query = query.Where(a => a.ContactId == q.ContactId);
        if (q.LeadId is not null) query = query.Where(a => a.LeadId == q.LeadId);
        if (q.OpportunityId is not null) query = query.Where(a => a.OpportunityId == q.OpportunityId);
        if (q.DueFrom is not null) query = query.Where(a => a.DueAt >= q.DueFrom);
        if (q.DueTo is not null) query = query.Where(a => a.DueAt <= q.DueTo);
        if (q.Mine) query = query.Where(a => a.OwnerId == user.GetUserId());
        else if (q.OwnerId is not null) query = query.Where(a => a.OwnerId == q.OwnerId);

        var desc = QueryableExtensions.IsDescending(q.SortDir);
        query = q.SortBy?.ToLowerInvariant() switch
        {
            "subject" => query.OrderByDir(a => a.Subject, desc),
            "priority" => query.OrderByDir(a => a.Priority, desc),
            "createdat" => query.OrderByDir(a => a.CreatedAt, desc),
            _ => desc
                ? query.OrderByDescending(a => a.DueAt)
                : query.OrderBy(a => a.DueAt == null).ThenBy(a => a.DueAt),
        };

        return TypedResults.Ok(await Project(query).ToPagedAsync(q.Page, q.PageSize, ct));
    }

    private static async Task<Results<Ok<ActivityItem>, NotFound>> Get(Guid id, CrmDbContext db, CancellationToken ct)
    {
        var item = await Project(db.Activities.AsNoTracking().Where(a => a.Id == id)).SingleOrDefaultAsync(ct);
        return item is null ? TypedResults.NotFound() : TypedResults.Ok(item);
    }

    private static async Task<Results<Created<ActivityItem>, ValidationProblem>> Create(
        ActivityRequest req, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        var me = user.GetUserId();
        if (await ValidateAsync(req, me, user, db, ct) is { } problem) return problem;

        var activity = new Activity { OwnerId = req.OwnerId ?? me };
        Apply(activity, req);
        db.Activities.Add(activity);
        await db.SaveChangesAsync(ct);

        var item = await Project(db.Activities.AsNoTracking().Where(a => a.Id == activity.Id)).SingleAsync(ct);
        return TypedResults.Created($"/api/activities/{activity.Id}", item);
    }

    private static async Task<Results<Ok<ActivityItem>, NotFound, ForbidHttpResult, ValidationProblem>> Update(
        Guid id, ActivityRequest req, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        var activity = await db.Activities.FindAsync([id], ct);
        if (activity is null) return TypedResults.NotFound();
        if (!user.CanModify(activity.OwnerId)) return TypedResults.Forbid();
        if (await ValidateAsync(req, activity.OwnerId, user, db, ct) is { } problem) return problem;

        Apply(activity, req);
        activity.OwnerId = req.OwnerId ?? activity.OwnerId;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await Project(db.Activities.AsNoTracking().Where(a => a.Id == id)).SingleAsync(ct));
    }

    private static async Task<Results<Ok<ActivityItem>, NotFound, ForbidHttpResult>> SetCompleted(
        Guid id, bool completed, ClaimsPrincipal user, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var activity = await db.Activities.FindAsync([id], ct);
        if (activity is null) return TypedResults.NotFound();
        if (!user.CanModify(activity.OwnerId)) return TypedResults.Forbid();

        activity.CompletedAt = completed ? clock.GetUtcNow().UtcDateTime : null;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await Project(db.Activities.AsNoTracking().Where(a => a.Id == id)).SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult>> Delete(
        Guid id, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        var activity = await db.Activities.FindAsync([id], ct);
        if (activity is null) return TypedResults.NotFound();
        if (!user.CanModify(activity.OwnerId)) return TypedResults.Forbid();
        db.Activities.Remove(activity);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<ValidationProblem?> ValidateAsync(ActivityRequest req, Guid currentOwner, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        if (req.AccountId is not null && !await db.Accounts.AnyAsync(x => x.Id == req.AccountId, ct))
            return Errors.Validation("accountId", "Account does not exist.");
        if (req.ContactId is not null && !await db.Contacts.AnyAsync(x => x.Id == req.ContactId, ct))
            return Errors.Validation("contactId", "Contact does not exist.");
        if (req.LeadId is not null && !await db.Leads.AnyAsync(x => x.Id == req.LeadId, ct))
            return Errors.Validation("leadId", "Lead does not exist.");
        if (req.OpportunityId is not null && !await db.Opportunities.AnyAsync(x => x.Id == req.OpportunityId, ct))
            return Errors.Validation("opportunityId", "Opportunity does not exist.");
        if (await Ownership.ValidateAsync(req.OwnerId, currentOwner, user, db, ct) is { } err)
            return Errors.Validation("ownerId", err);
        return null;
    }

    private static void Apply(Activity a, ActivityRequest r)
    {
        a.Type = r.Type;
        a.Subject = r.Subject.Trim();
        a.Description = r.Description;
        a.DueAt = r.DueAt;
        a.Priority = r.Priority;
        a.AccountId = r.AccountId;
        a.ContactId = r.ContactId;
        a.LeadId = r.LeadId;
        a.OpportunityId = r.OpportunityId;
    }

    public static IQueryable<ActivityItem> Project(IQueryable<Activity> q) => q.Select(a => new ActivityItem(
        a.Id, a.Type, a.Subject, a.Description, a.DueAt, a.CompletedAt, a.Priority,
        a.AccountId, a.Account != null ? a.Account.Name : null,
        a.ContactId, a.Contact != null ? a.Contact.FirstName + " " + a.Contact.LastName : null,
        a.LeadId, a.Lead != null ? a.Lead.FirstName + " " + a.Lead.LastName : null,
        a.OpportunityId, a.Opportunity != null ? a.Opportunity.Name : null,
        a.OwnerId, a.Owner!.FirstName + " " + a.Owner.LastName, a.CreatedAt, a.UpdatedAt));
}
