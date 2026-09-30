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

namespace Crm.Api.Features.Contacts;

public record ContactListItem(
    Guid Id, string FirstName, string LastName, string FullName, string? Email, string? Phone, string? JobTitle,
    Guid? AccountId, string? AccountName, Guid OwnerId, string OwnerName, DateTime CreatedAt, JsonObject CustomFields);

public record ContactDetail(
    Guid Id, string FirstName, string LastName, string FullName, string? Email, string? Phone, string? Mobile,
    string? JobTitle, string? Department, string? Description, Guid? AccountId, string? AccountName,
    Guid OwnerId, string OwnerName, DateTime CreatedAt, DateTime UpdatedAt, JsonObject CustomFields);

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
    Guid? OwnerId,
    JsonObject? CustomFields = null);

public record ContactQuery(
    string? Search, Guid? AccountId, Guid? OwnerId, bool Mine = false,
    int Page = 1, int PageSize = 20, string? SortBy = null, string? SortDir = null);

public static class ContactEndpoints
{
    private const string R = ObjectKeys.Contact;

    public static RouteGroupBuilder MapContacts(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/contacts").WithTags("Contacts");
        g.MapGet("/", List).RequirePermission(R, Read);
        g.MapGet("/{id:guid}", Get).RequirePermission(R, Read);
        g.MapPost("/", CreateAsync).RequirePermission(R, Create);
        g.MapPut("/{id:guid}", UpdateAsync).RequirePermission(R, Update);
        g.MapDelete("/{id:guid}", DeleteAsync).RequirePermission(R, Delete);
        return api;
    }

    private static async Task<Ok<PagedResult<ContactListItem>>> List(
        [AsParameters] ContactQuery q, HttpRequest http, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var query = p.Filter(db.Contacts.AsNoTracking(), R).WhereCustomFields(http.Query, db);
        var search = QueryableExtensions.NormalizeSearch(q.Search);
        if (search is not null)
            query = query.Where(c => (c.FirstName + " " + c.LastName).ToLower().Contains(search)
                || (c.Email != null && c.Email.ToLower().Contains(search))
                || (c.Account != null && c.Account.Name.ToLower().Contains(search)));
        if (q.AccountId is not null) query = query.Where(c => c.AccountId == q.AccountId);
        if (q.Mine) query = query.Where(c => c.OwnerId == p.UserId);
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
            c.OwnerId, c.Owner!.FirstName + " " + c.Owner.LastName, c.CreatedAt, Cf.Parse(c.CustomFields)))
            .ToPagedAsync(q.Page, q.PageSize, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<ContactDetail>, NotFound>> Get(Guid id, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var detail = await Project(p.Filter(db.Contacts.AsNoTracking(), R).Where(c => c.Id == id)).SingleOrDefaultAsync(ct);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    private static async Task<Results<Created<ContactDetail>, ValidationProblem>> CreateAsync(
        ContactRequest req, Access access, CrmDbContext db, CustomFieldValidator validator, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var contact = new Contact { OwnerId = req.OwnerId ?? p.UserId };
        if (await ValidateAsync(req, contact, p.UserId, p, db, validator, ct) is { } problem) return problem;

        db.Contacts.Add(contact);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/contacts/{contact.Id}", await Project(db.Contacts.AsNoTracking().Where(c => c.Id == contact.Id)).SingleAsync(ct));
    }

    private static async Task<Results<Ok<ContactDetail>, NotFound, ForbidHttpResult, ValidationProblem>> UpdateAsync(
        Guid id, ContactRequest req, Access access, CrmDbContext db, CustomFieldValidator validator, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var contact = await db.Contacts.FindAsync([id], ct);
        if (contact is null || !p.CanAccess(R, Read, contact.OwnerId)) return TypedResults.NotFound();
        if (!p.CanAccess(R, Update, contact.OwnerId)) return TypedResults.Forbid();
        if (await ValidateAsync(req, contact, contact.OwnerId, p, db, validator, ct) is { } problem) return problem;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await Project(db.Contacts.AsNoTracking().Where(c => c.Id == id)).SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult>> DeleteAsync(Guid id, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var contact = await db.Contacts.FindAsync([id], ct);
        if (contact is null || !p.CanAccess(R, Read, contact.OwnerId)) return TypedResults.NotFound();
        if (!p.CanAccess(R, Delete, contact.OwnerId)) return TypedResults.Forbid();
        db.Contacts.Remove(contact);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<ValidationProblem?> ValidateAsync(ContactRequest r, Contact c, Guid currentOwner, EffectivePermissions p,
        CrmDbContext db, CustomFieldValidator validator, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (r.AccountId is not null && !await db.Accounts.AnyAsync(a => a.Id == r.AccountId, ct)) errors["accountId"] = ["Account does not exist."];
        if (await Ownership.ValidateAsync(r.OwnerId, currentOwner, R, p, db, ct) is { } ownerError) errors["ownerId"] = [ownerError];
        var cf = await validator.ValidateAsync(R, r.CustomFields, c.CustomFields, ct);
        foreach (var (k, v) in cf.Errors) errors[k] = v;
        if (errors.Count > 0) return Errors.Validation(errors);

        c.FirstName = r.FirstName.Trim();
        c.LastName = r.LastName.Trim();
        c.Email = r.Email;
        c.Phone = r.Phone;
        c.Mobile = r.Mobile;
        c.JobTitle = r.JobTitle;
        c.Department = r.Department;
        c.Description = r.Description;
        c.AccountId = r.AccountId;
        c.OwnerId = r.OwnerId ?? c.OwnerId;
        c.CustomFields = cf.Json;
        return null;
    }

    private static IQueryable<ContactDetail> Project(IQueryable<Contact> q) => q.Select(c => new ContactDetail(
        c.Id, c.FirstName, c.LastName, c.FirstName + " " + c.LastName, c.Email, c.Phone, c.Mobile,
        c.JobTitle, c.Department, c.Description, c.AccountId, c.Account != null ? c.Account.Name : null,
        c.OwnerId, c.Owner!.FirstName + " " + c.Owner.LastName, c.CreatedAt, c.UpdatedAt, Cf.Parse(c.CustomFields)));
}
