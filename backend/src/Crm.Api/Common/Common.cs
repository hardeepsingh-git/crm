using System.Linq.Expressions;
using System.Security.Claims;
using Crm.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Common;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string SalesRep = "SalesRep";
    public static readonly string[] All = [Admin, Manager, SalesRep];
}

public static class Policies
{
    public const string AdminOnly = "AdminOnly";
    public const string ManagerOrAdmin = "ManagerOrAdmin";
}

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue("sub") ?? throw new InvalidOperationException("Missing sub claim."));

    public static bool IsManagerOrAdmin(this ClaimsPrincipal user) =>
        user.IsInRole(Roles.Admin) || user.IsInRole(Roles.Manager);

    public static bool CanModify(this ClaimsPrincipal user, Guid ownerId) =>
        user.IsManagerOrAdmin() || user.GetUserId() == ownerId;
}

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

public static class QueryableExtensions
{
    public const int MaxPageSize = 100;

    public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> query, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<T>(items, total, page, pageSize);
    }

    public static IOrderedQueryable<T> OrderByDir<T, TKey>(this IQueryable<T> query, Expression<Func<T, TKey>> key, bool descending) =>
        descending ? query.OrderByDescending(key) : query.OrderBy(key);

    public static bool IsDescending(string? sortDir) => string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);

    public static string? NormalizeSearch(string? search) =>
        string.IsNullOrWhiteSpace(search) ? null : search.Trim().ToLower();
}

public static class Errors
{
    public static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem Validation(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}

public static class Ownership
{
    /// <summary>Returns an error message if the caller may not assign <paramref name="requested"/> as owner.</summary>
    public static async Task<string?> ValidateAsync(Guid? requested, Guid currentOwner, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        if (requested is null || requested == currentOwner) return null;
        if (!user.IsManagerOrAdmin() && requested != user.GetUserId())
            return "Only managers and admins can assign records to other users.";
        var exists = await db.Users.AnyAsync(u => u.Id == requested && u.IsActive, ct);
        return exists ? null : "Owner does not exist or is inactive.";
    }
}
