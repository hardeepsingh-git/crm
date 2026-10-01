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

namespace Crm.Api.Features.Activities;

public record ActivityItem(
    Guid Id, ActivityType Type, string Subject, string? Description, DateTime? DueAt, DateTime? CompletedAt,
    ActivityPriority Priority, Guid? AccountId, string? AccountName, Guid? ContactId, string? ContactName,
    Guid? LeadId, string? LeadName, Guid? OpportunityId, string? OpportunityName,
    Guid? CustomRecordId, string? CustomRecordName, string? CustomObjectKey,
    Guid OwnerId, string OwnerName, DateTime CreatedAt, DateTime UpdatedAt, JsonObject CustomFields);

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
    Guid? OwnerId,
    Guid? CustomRecordId = null,
    JsonObject? CustomFields = null);

public enum ActivityStatusFilter { Open, Completed, Overdue }

public record ActivityQuery(
    string? Search, ActivityType? Type, ActivityStatusFilter? Status,
    Guid? AccountId, Guid? ContactId, Guid? LeadId, Guid? OpportunityId, Guid? CustomRecordId, Guid? OwnerId,
    DateTime? DueFrom, DateTime? DueTo, bool Mine = false,
    int Page = 1, int PageSize = 20, string? SortBy = null, string? SortDir = null);

public static class ActivityEndpoints
{
    private const string R = ObjectKeys.Activity;

    public static RouteGroupBuilder MapActivities(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/activities").WithTags("Activities");
        g.MapGet("/", List).RequirePermission(R, Read);
        g.MapGet("/{id:guid}", Get).RequirePermission(R, Read);
        g.MapPost("/", CreateAsync).RequirePermission(R, Create);
        g.MapPut("/{id:guid}", UpdateAsync).RequirePermission(R, Update);
        g.MapPost("/{id:guid}/complete", (Guid id, Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct)
            => SetCompleted(id, true, access, db, clock, ct)).RequirePermission(R, Update);
        g.MapPost("/{id:guid}/reopen", (Guid id, Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct)
            => SetCompleted(id, false, access, db, clock, ct)).RequirePermission(R, Update);
        g.MapDelete("/{id:guid}", DeleteAsync).RequirePermission(R, Delete);
        return api;
    }

    private static async Task<Ok<PagedResult<ActivityItem>>> List(
        [AsParameters] ActivityQuery q, HttpRequest http, Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var query = p.Filter(db.Activities.AsNoTracking(), R).WhereCustomFields(http.Query, db);
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
        if (q.CustomRecordId is not null) query = query.Where(a => a.CustomRecordId == q.CustomRecordId);
        if (q.DueFrom is not null) query = query.Where(a => a.DueAt >= q.DueFrom);
        if (q.DueTo is not null) query = query.Where(a => a.DueAt <= q.DueTo);
        if (q.Mine) query = query.Where(a => a.OwnerId == p.UserId);
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

    private static async Task<Results<Ok<ActivityItem>, NotFound>> Get(Guid id, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var item = await Project(p.Filter(db.Activities.AsNoTracking(), R).Where(a => a.Id == id)).SingleOrDefaultAsync(ct);
        return item is null ? TypedResults.NotFound() : TypedResults.Ok(item);
    }

    private static async Task<Results<Created<ActivityItem>, ValidationProblem>> CreateAsync(
        ActivityRequest req, Access access, CrmDbContext db, CustomFieldValidator validator, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var activity = new Activity { OwnerId = req.OwnerId ?? p.UserId };
        if (await ValidateAsync(req, activity, p.UserId, p, db, validator, ct) is { } problem) return problem;

        db.Activities.Add(activity);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/activities/{activity.Id}", await Project(db.Activities.AsNoTracking().Where(a => a.Id == activity.Id)).SingleAsync(ct));
    }

    private static async Task<Results<Ok<ActivityItem>, NotFound, ForbidHttpResult, ValidationProblem>> UpdateAsync(
        Guid id, ActivityRequest req, Access access, CrmDbContext db, CustomFieldValidator validator, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var activity = await db.Activities.FindAsync([id], ct);
        if (activity is null || !p.CanAccess(R, Read, activity.OwnerId)) return TypedResults.NotFound();
        if (!p.CanAccess(R, Update, activity.OwnerId)) return TypedResults.Forbid();
        if (await ValidateAsync(req, activity, activity.OwnerId, p, db, validator, ct) is { } problem) return problem;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await Project(db.Activities.AsNoTracking().Where(a => a.Id == id)).SingleAsync(ct));
    }

    private static async Task<Results<Ok<ActivityItem>, NotFound, ForbidHttpResult>> SetCompleted(
        Guid id, bool completed, Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var activity = await db.Activities.FindAsync([id], ct);
        if (activity is null || !p.CanAccess(R, Read, activity.OwnerId)) return TypedResults.NotFound();
        if (!p.CanAccess(R, Update, activity.OwnerId)) return TypedResults.Forbid();

        activity.CompletedAt = completed ? clock.GetUtcNow().UtcDateTime : null;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await Project(db.Activities.AsNoTracking().Where(a => a.Id == id)).SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult>> DeleteAsync(Guid id, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var activity = await db.Activities.FindAsync([id], ct);
        if (activity is null || !p.CanAccess(R, Read, activity.OwnerId)) return TypedResults.NotFound();
        if (!p.CanAccess(R, Delete, activity.OwnerId)) return TypedResults.Forbid();
        db.Activities.Remove(activity);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<ValidationProblem?> ValidateAsync(ActivityRequest r, Activity a, Guid currentOwner, EffectivePermissions p,
        CrmDbContext db, CustomFieldValidator validator, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (r.AccountId is not null && !await db.Accounts.AnyAsync(x => x.Id == r.AccountId, ct)) errors["accountId"] = ["Account does not exist."];
        if (r.ContactId is not null && !await db.Contacts.AnyAsync(x => x.Id == r.ContactId, ct)) errors["contactId"] = ["Contact does not exist."];
        if (r.LeadId is not null && !await db.Leads.AnyAsync(x => x.Id == r.LeadId, ct)) errors["leadId"] = ["Lead does not exist."];
        if (r.OpportunityId is not null && !await db.Opportunities.AnyAsync(x => x.Id == r.OpportunityId, ct)) errors["opportunityId"] = ["Opportunity does not exist."];
        if (r.CustomRecordId is not null && !await db.CustomRecords.AnyAsync(x => x.Id == r.CustomRecordId, ct)) errors["customRecordId"] = ["Record does not exist."];
        if (await Ownership.ValidateAsync(r.OwnerId, currentOwner, R, p, db, ct) is { } ownerError) errors["ownerId"] = [ownerError];
        var cf = await validator.ValidateAsync(R, r.CustomFields, a.CustomFields, a.Id, ct);
        foreach (var (k, v) in cf.Errors) errors[k] = v;
        if (errors.Count > 0) return Errors.Validation(errors);

        a.Type = r.Type;
        a.Subject = r.Subject.Trim();
        a.Description = r.Description;
        a.DueAt = r.DueAt;
        a.Priority = r.Priority;
        a.AccountId = r.AccountId;
        a.ContactId = r.ContactId;
        a.LeadId = r.LeadId;
        a.OpportunityId = r.OpportunityId;
        a.CustomRecordId = r.CustomRecordId;
        a.OwnerId = r.OwnerId ?? a.OwnerId;
        a.CustomFields = cf.Json;
        return null;
    }

    public static IQueryable<ActivityItem> Project(IQueryable<Activity> q) => q.Select(a => new ActivityItem(
        a.Id, a.Type, a.Subject, a.Description, a.DueAt, a.CompletedAt, a.Priority,
        a.AccountId, a.Account != null ? a.Account.Name : null,
        a.ContactId, a.Contact != null ? a.Contact.FirstName + " " + a.Contact.LastName : null,
        a.LeadId, a.Lead != null ? a.Lead.FirstName + " " + a.Lead.LastName : null,
        a.OpportunityId, a.Opportunity != null ? a.Opportunity.Name : null,
        a.CustomRecordId, a.CustomRecord != null ? a.CustomRecord.Name : null,
        a.CustomRecord != null ? a.CustomRecord.ObjectDefinition!.Key : null,
        a.OwnerId, a.Owner!.FirstName + " " + a.Owner.LastName, a.CreatedAt, a.UpdatedAt, Cf.Parse(a.CustomFields)));
}
