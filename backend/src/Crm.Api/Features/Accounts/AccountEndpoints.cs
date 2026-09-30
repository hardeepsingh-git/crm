using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Accounts;

public record AccountListItem(
    Guid Id, string Name, string? Industry, string? Phone, string? Email, string? City, string? Country,
    Guid OwnerId, string OwnerName, int ContactCount, int OpenOpportunityCount, DateTime CreatedAt);

public record AccountDetail(
    Guid Id, string Name, string? Industry, string? Website, string? Phone, string? Email,
    string? Street, string? City, string? State, string? PostalCode, string? Country,
    decimal? AnnualRevenue, int? EmployeeCount, string? Description,
    Guid OwnerId, string OwnerName, int ContactCount, int OpenOpportunityCount, decimal OpenPipelineAmount,
    DateTime CreatedAt, DateTime UpdatedAt);

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
    Guid? OwnerId);

public record AccountQuery(
    string? Search, string? Industry, Guid? OwnerId, bool Mine = false,
    int Page = 1, int PageSize = 20, string? SortBy = null, string? SortDir = null);

public static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccounts(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/accounts").WithTags("Accounts");
        g.MapGet("/", List);
        g.MapGet("/{id:guid}", Get);
        g.MapPost("/", Create);
        g.MapPut("/{id:guid}", Update);
        g.MapDelete("/{id:guid}", Delete).RequireAuthorization(Policies.ManagerOrAdmin);
        return api;
    }

    private static async Task<Ok<PagedResult<AccountListItem>>> List(
        [AsParameters] AccountQuery q, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        var query = db.Accounts.AsNoTracking();
        var search = QueryableExtensions.NormalizeSearch(q.Search);
        if (search is not null)
            query = query.Where(a => a.Name.ToLower().Contains(search)
                || (a.Email != null && a.Email.ToLower().Contains(search))
                || (a.City != null && a.City.ToLower().Contains(search)));
        if (!string.IsNullOrWhiteSpace(q.Industry)) query = query.Where(a => a.Industry == q.Industry);
        if (q.Mine) query = query.Where(a => a.OwnerId == user.GetUserId());
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
            a.Opportunities.Count(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost),
            a.CreatedAt)).ToPagedAsync(q.Page, q.PageSize, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<AccountDetail>, NotFound>> Get(Guid id, CrmDbContext db, CancellationToken ct)
    {
        var detail = await Project(db.Accounts.AsNoTracking().Where(a => a.Id == id)).SingleOrDefaultAsync(ct);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    private static async Task<Results<Created<AccountDetail>, ValidationProblem>> Create(
        AccountRequest req, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        var me = user.GetUserId();
        if (await Ownership.ValidateAsync(req.OwnerId, me, user, db, ct) is { } err) return Errors.Validation("ownerId", err);

        var account = new Account { OwnerId = req.OwnerId ?? me };
        Apply(account, req);
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);

        var detail = await Project(db.Accounts.AsNoTracking().Where(a => a.Id == account.Id)).SingleAsync(ct);
        return TypedResults.Created($"/api/accounts/{account.Id}", detail);
    }

    private static async Task<Results<Ok<AccountDetail>, NotFound, ForbidHttpResult, ValidationProblem>> Update(
        Guid id, AccountRequest req, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        var account = await db.Accounts.FindAsync([id], ct);
        if (account is null) return TypedResults.NotFound();
        if (!user.CanModify(account.OwnerId)) return TypedResults.Forbid();
        if (await Ownership.ValidateAsync(req.OwnerId, account.OwnerId, user, db, ct) is { } err) return Errors.Validation("ownerId", err);

        Apply(account, req);
        account.OwnerId = req.OwnerId ?? account.OwnerId;
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await Project(db.Accounts.AsNoTracking().Where(a => a.Id == id)).SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound, Conflict<string>>> Delete(Guid id, CrmDbContext db, CancellationToken ct)
    {
        var account = await db.Accounts.FindAsync([id], ct);
        if (account is null) return TypedResults.NotFound();
        if (await db.Opportunities.AnyAsync(o => o.AccountId == id, ct))
            return TypedResults.Conflict("Account has opportunities. Delete or reassign them first.");

        db.Accounts.Remove(account);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static void Apply(Account a, AccountRequest r)
    {
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
    }

    private static IQueryable<AccountDetail> Project(IQueryable<Account> q) => q.Select(a => new AccountDetail(
        a.Id, a.Name, a.Industry, a.Website, a.Phone, a.Email,
        a.Street, a.City, a.State, a.PostalCode, a.Country,
        a.AnnualRevenue, a.EmployeeCount, a.Description,
        a.OwnerId, a.Owner!.FirstName + " " + a.Owner.LastName,
        a.Contacts.Count,
        a.Opportunities.Count(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost),
        a.Opportunities.Where(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost).Sum(o => (decimal?)o.Amount) ?? 0,
        a.CreatedAt, a.UpdatedAt));
}
