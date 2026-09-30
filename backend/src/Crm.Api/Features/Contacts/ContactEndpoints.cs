using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Contacts;

public record ContactListItem(
    Guid Id, string FirstName, string LastName, string FullName, string? Email, string? Phone, string? JobTitle,
    Guid? AccountId, string? AccountName, Guid OwnerId, string OwnerName, DateTime CreatedAt);

public record ContactDetail(
    Guid Id, string FirstName, string LastName, string FullName, string? Email, string? Phone, string? Mobile,
    string? JobTitle, string? Department, string? Description, Guid? AccountId, string? AccountName,
    Guid OwnerId, string OwnerName, DateTime CreatedAt, DateTime UpdatedAt);

public record ContactRequest(
    [Required, MaxLength(100)] string FirstName,
    [Required, MaxLength(100)] string LastName,
    [EmailAddress, MaxLength(256)] string? Email,
    [MaxLength(50)] string? Phone,
    [MaxLength(50)] string? Mobile,
    [MaxLength(100)] string? JobTitle,
    [MaxLength(100)] string? Department,
    [MaxLength(4000)] string? Description,
    Guid? AccountId,
    Guid? OwnerId);

public record ContactQuery(
    string? Search, Guid? AccountId, Guid? OwnerId, bool Mine = false,
    int Page = 1, int PageSize = 20, string? SortBy = null, string? SortDir = null);

public static class ContactEndpoints
{
    public static RouteGroupBuilder MapContacts(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/contacts").WithTags("Contacts");
        g.MapGet("/", List);
        g.MapGet("/{id:guid}", Get);
        g.MapPost("/", Create);
        g.MapPut("/{id:guid}", Update);
        g.MapDelete("/{id:guid}", Delete).RequireAuthorization(Policies.ManagerOrAdmin);
        return api;
    }

    private static async Task<Ok<PagedResult<ContactListItem>>> List(
        [AsParameters] ContactQuery q, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        var query = db.Contacts.AsNoTracking();
        var search = QueryableExtensions.NormalizeSearch(q.Search);
        if (search is not null)
            query = query.Where(c => (c.FirstName + " " + c.LastName).ToLower().Contains(search)
                || (c.Email != null && c.Email.ToLower().Contains(search))
                || (c.Account != null && c.Account.Name.ToLower().Contains(search)));
        if (q.AccountId is not null) query = query.Where(c => c.AccountId == q.AccountId);
        if (q.Mine) query = query.Where(c => c.OwnerId == user.GetUserId());
        else if (q.OwnerId is not null) query = query.Where(c => c.OwnerId == q.OwnerId);

        var desc = QueryableExtensions.IsDescending(q.SortDir);
        query = q.SortBy?.ToLowerInvariant() switch
        {
            "firstname" => query.OrderByDir(c => c.FirstName, desc),
            "email" => query.OrderByDir(c => c.Email, desc),
            "account" => query.OrderByDir(c => c.Account!.Name, desc),
            "createdat" => query.OrderByDir(c => c.CreatedAt, desc),
            _ => query.OrderByDir(c => c.LastName, desc).ThenBy(c => c.FirstName),
        };

        var result = await query.Select(c => new ContactListItem(
            c.Id, c.FirstName, c.LastName, c.FirstName + " " + c.LastName, c.Email, c.Phone, c.JobTitle,
            c.AccountId, c.Account != null ? c.Account.Name : null,
            c.OwnerId, c.Owner!.FirstName + " " + c.Owner.LastName, c.CreatedAt)).ToPagedAsync(q.Page, q.PageSize, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<ContactDetail>, NotFound>> Get(Guid id, CrmDbContext db, CancellationToken ct)
    {
        var detail = await Project(db.Contacts.AsNoTracking().Where(c => c.Id == id)).SingleOrDefaultAsync(ct);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    private static async Task<Results<Created<ContactDetail>, ValidationProblem>> Create(
        ContactRequest req, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        var me = user.GetUserId();
        if (await ValidateAsync(req, me, user, db, ct) is { } problem) return problem;

        var contact = new Contact { OwnerId = req.OwnerId ?? me };
        Apply(contact, req);
        db.Contacts.Add(contact);
        await db.SaveChangesAsync(ct);

        var detail = await Project(db.Contacts.AsNoTracking().Where(c => c.Id == contact.Id)).SingleAsync(ct);
        return TypedResults.Created($"/api/contacts/{contact.Id}", detail);
    }

    private static async Task<Results<Ok<ContactDetail>, NotFound, ForbidHttpResult, ValidationProblem>> Update(
        Guid id, ContactRequest req, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        var contact = await db.Contacts.FindAsync([id], ct);
        if (contact is null) return TypedResults.NotFound();
        if (!user.CanModify(contact.OwnerId)) return TypedResults.Forbid();
        if (await ValidateAsync(req, contact.OwnerId, user, db, ct) is { } problem) return problem;

        Apply(contact, req);
        contact.OwnerId = req.OwnerId ?? contact.OwnerId;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await Project(db.Contacts.AsNoTracking().Where(c => c.Id == id)).SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound>> Delete(Guid id, CrmDbContext db, CancellationToken ct)
    {
        var contact = await db.Contacts.FindAsync([id], ct);
        if (contact is null) return TypedResults.NotFound();
        db.Contacts.Remove(contact);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<ValidationProblem?> ValidateAsync(ContactRequest req, Guid currentOwner, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        if (req.AccountId is not null && !await db.Accounts.AnyAsync(a => a.Id == req.AccountId, ct))
            return Errors.Validation("accountId", "Account does not exist.");
        if (await Ownership.ValidateAsync(req.OwnerId, currentOwner, user, db, ct) is { } err)
            return Errors.Validation("ownerId", err);
        return null;
    }

    private static void Apply(Contact c, ContactRequest r)
    {
        c.FirstName = r.FirstName.Trim();
        c.LastName = r.LastName.Trim();
        c.Email = r.Email;
        c.Phone = r.Phone;
        c.Mobile = r.Mobile;
        c.JobTitle = r.JobTitle;
        c.Department = r.Department;
        c.Description = r.Description;
        c.AccountId = r.AccountId;
    }

    private static IQueryable<ContactDetail> Project(IQueryable<Contact> q) => q.Select(c => new ContactDetail(
        c.Id, c.FirstName, c.LastName, c.FirstName + " " + c.LastName, c.Email, c.Phone, c.Mobile,
        c.JobTitle, c.Department, c.Description, c.AccountId, c.Account != null ? c.Account.Name : null,
        c.OwnerId, c.Owner!.FirstName + " " + c.Owner.LastName, c.CreatedAt, c.UpdatedAt));
}
