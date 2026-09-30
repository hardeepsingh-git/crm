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

namespace Crm.Api.Features.Accounts;

public record AccountListItem(
    Guid Id, string Name, string? Industry, string? Phone, string? Email, string? City, string? Country,
    Guid OwnerId, string OwnerName, int ContactCount, int OpenOpportunityCount, DateTime CreatedAt, JsonObject CustomFields);

public record AccountDetail(
    Guid Id, string Name, string? Industry, string? Website, string? Phone, string? Email,
    string? Street, string? City, string? State, string? PostalCode, string? Country,
    decimal? AnnualRevenue, int? EmployeeCount, string? Description,
    Guid OwnerId, string OwnerName, int ContactCount, int OpenOpportunityCount, decimal OpenPipelineAmount,
    DateTime CreatedAt, DateTime UpdatedAt, JsonObject CustomFields);

public record AccountRequest(
    [Required, MaxLength(200)] string Name,
    [MaxLength(100)] string? Industry,
    [Url, MaxLength(300)] string? Website,
    [MaxLength(50)] string? Phone,
    [EmailAddress, MaxLength(256)] string? Email,
    [MaxLength(200)] string? Street,
    [MaxLength(100)] string? City,
    [MaxLength(100)] string? State,
    [MaxLength(20)] string? PostalCode,
    [MaxLength(100)] string? Country,
    [Range(0, 1_000_000_000_000)] decimal? AnnualRevenue,
    [Range(0, int.MaxValue)] int? EmployeeCount,
    [MaxLength(4000)] string? Description,
    Guid? OwnerId,
    JsonObject? CustomFields = null);

public record AccountQuery(
    string? Search, string? Industry, Guid? OwnerId, bool Mine = false,
    int Page = 1, int PageSize = 20, string? SortBy = null, string? SortDir = null);

public static class AccountEndpoints
{
    private const string R = ObjectKeys.Account;

    public static RouteGroupBuilder MapAccounts(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/accounts").WithTags("Accounts");
        g.MapGet("/", List).RequirePermission(R, Read);
        g.MapGet("/{id:guid}", Get).RequirePermission(R, Read);
        g.MapPost("/", CreateAsync).RequirePermission(R, Create);
        g.MapPut("/{id:guid}", UpdateAsync).RequirePermission(R, Update);
        g.MapDelete("/{id:guid}", DeleteAsync).RequirePermission(R, Delete);
        return api;
    }

    private static async Task<Ok<PagedResult<AccountListItem>>> List(
        [AsParameters] AccountQuery q, HttpRequest http, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var query = p.Filter(db.Accounts.AsNoTracking(), R).WhereCustomFields(http.Query, db);
        var search = QueryableExtensions.NormalizeSearch(q.Search);
        if (search is not null)
            query = query.Where(a => a.Name.ToLower().Contains(search)
                || (a.Email != null && a.Email.ToLower().Contains(search))
                || (a.City != null && a.City.ToLower().Contains(search)));
        if (!string.IsNullOrWhiteSpace(q.Industry)) query = query.Where(a => a.Industry == q.Industry);
        if (q.Mine) query = query.Where(a => a.OwnerId == p.UserId);
        else if (q.OwnerId is not null) query = query.Where(a => a.OwnerId == q.OwnerId);

        var desc = QueryableExtensions.IsDescending(q.SortDir);
        query = q.SortBy?.ToLowerInvariant() switch
        {
            "industry" => query.OrderByDir(a => a.Industry, desc),
            "city" => query.OrderByDir(a => a.City, desc),
            "createdat" => query.OrderByDir(a => a.CreatedAt, desc),
            _ => query.OrderByDir(a => a.Name, desc),
        };

        var result = await query.Select(a => new AccountListItem(
            a.Id, a.Name, a.Industry, a.Phone, a.Email, a.City, a.Country,
            a.OwnerId, a.Owner!.FirstName + " " + a.Owner.LastName,
            a.Contacts.Count,
            db.Opportunities.Count(o => o.AccountId == a.Id && o.ClosedAt == null),
            a.CreatedAt, Cf.Parse(a.CustomFields))).ToPagedAsync(q.Page, q.PageSize, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<AccountDetail>, NotFound>> Get(Guid id, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var detail = await Project(p.Filter(db.Accounts.AsNoTracking(), R).Where(a => a.Id == id), db).SingleOrDefaultAsync(ct);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    private static async Task<Results<Created<AccountDetail>, ValidationProblem>> CreateAsync(
        AccountRequest req, Access access, CrmDbContext db, MetadataService meta, CustomFieldValidator validator, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var account = new Account { OwnerId = req.OwnerId ?? p.UserId };
        if (await ValidateAsync(req, account, p.UserId, p, db, meta, validator, ct) is { } problem) return problem;

        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/accounts/{account.Id}", await Project(db.Accounts.AsNoTracking().Where(a => a.Id == account.Id), db).SingleAsync(ct));
    }

    private static async Task<Results<Ok<AccountDetail>, NotFound, ForbidHttpResult, ValidationProblem>> UpdateAsync(
        Guid id, AccountRequest req, Access access, CrmDbContext db, MetadataService meta, CustomFieldValidator validator, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var account = await db.Accounts.FindAsync([id], ct);
        if (account is null || !p.CanAccess(R, Read, account.OwnerId)) return TypedResults.NotFound();
        if (!p.CanAccess(R, Update, account.OwnerId)) return TypedResults.Forbid();
        if (await ValidateAsync(req, account, account.OwnerId, p, db, meta, validator, ct) is { } problem) return problem;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await Project(db.Accounts.AsNoTracking().Where(a => a.Id == id), db).SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult, Conflict<string>>> DeleteAsync(
        Guid id, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var account = await db.Accounts.FindAsync([id], ct);
        if (account is null || !p.CanAccess(R, Read, account.OwnerId)) return TypedResults.NotFound();
        if (!p.CanAccess(R, Delete, account.OwnerId)) return TypedResults.Forbid();
        if (await db.Opportunities.AnyAsync(o => o.AccountId == id, ct))
            return TypedResults.Conflict("Account has opportunities. Delete or reassign them first.");

        db.Accounts.Remove(account);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Validates the request and, when valid, applies it to <paramref name="a"/>.</summary>
    private static async Task<ValidationProblem?> ValidateAsync(AccountRequest r, Account a, Guid currentOwner, EffectivePermissions p,
        CrmDbContext db, MetadataService meta, CustomFieldValidator validator, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (await Ownership.ValidateAsync(r.OwnerId, currentOwner, R, p, db, ct) is { } ownerError) errors["ownerId"] = [ownerError];
        if (await meta.CheckPicklistAsync("account.industry", r.Industry, a.Industry, ct) is { } industryError) errors["industry"] = [industryError];
        var cf = await validator.ValidateAsync(R, r.CustomFields, a.CustomFields, ct);
        foreach (var (k, v) in cf.Errors) errors[k] = v;
        if (errors.Count > 0) return Errors.Validation(errors);

        a.Name = r.Name.Trim();
        a.Industry = r.Industry;
        a.Website = r.Website;
        a.Phone = r.Phone;
        a.Email = r.Email;
        a.Street = r.Street;
        a.City = r.City;
        a.State = r.State;
        a.PostalCode = r.PostalCode;
        a.Country = r.Country;
        a.AnnualRevenue = r.AnnualRevenue;
        a.EmployeeCount = r.EmployeeCount;
        a.Description = r.Description;
        a.OwnerId = r.OwnerId ?? a.OwnerId;
        a.CustomFields = cf.Json;
        return null;
    }

    private static IQueryable<AccountDetail> Project(IQueryable<Account> q, CrmDbContext db) => q.Select(a => new AccountDetail(
        a.Id, a.Name, a.Industry, a.Website, a.Phone, a.Email,
        a.Street, a.City, a.State, a.PostalCode, a.Country,
        a.AnnualRevenue, a.EmployeeCount, a.Description,
        a.OwnerId, a.Owner!.FirstName + " " + a.Owner.LastName,
        a.Contacts.Count,
        db.Opportunities.Count(o => o.AccountId == a.Id && o.ClosedAt == null),
        db.Opportunities.Where(o => o.AccountId == a.Id && o.ClosedAt == null).Sum(o => (decimal?)o.Amount) ?? 0,
        a.CreatedAt, a.UpdatedAt, Cf.Parse(a.CustomFields)));
}
