using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Common;

/// <summary>The tenant and user the current request (or background operation) acts for.</summary>
public sealed class TenantContext
{
    public Guid? TenantId { get; private set; }
    public Guid? UserId { get; private set; }
    public string? UserName { get; private set; }

    public void Set(Guid tenantId, Guid? userId = null, string? userName = null)
    {
        TenantId = tenantId;
        UserId = userId;
        UserName = userName;
    }

    /// <summary>Temporarily acts inside another tenant, e.g. when a platform admin provisions a new tenant.</summary>
    public IDisposable Use(Guid tenantId, Guid? userId = null, string? userName = null)
    {
        var previous = (TenantId, UserId, UserName);
        Set(tenantId, userId ?? UserId, userName ?? UserName);
        return new Restore(() => (TenantId, UserId, UserName) = previous);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

public static class Claims
{
    public const string Subject = "sub";
    public const string Tenant = "tid";
    public const string Name = "name";
    public const string Email = "email";
}

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(Claims.Subject) ?? throw new InvalidOperationException("Missing sub claim."));
}

/// <summary>Populates <see cref="TenantContext"/> from the access token. Tokens without a tenant claim are rejected.</summary>
public sealed class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            if (!Guid.TryParse(user.FindFirstValue(Claims.Tenant), out var tenantId) ||
                !Guid.TryParse(user.FindFirstValue(Claims.Subject), out var userId))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            tenant.Set(tenantId, userId, user.FindFirstValue(Claims.Name));
        }
        await next(context);
    }
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
    public static ValidationProblem Validation(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    public static ValidationProblem Validation(IDictionary<string, string[]> errors) => TypedResults.ValidationProblem(errors);
}

/// <summary>Helpers for the JSON object stored in <c>CustomFields</c> columns.</summary>
public static class Cf
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static JsonObject Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonNode.Parse(json) as JsonObject ?? []; }
        catch (JsonException) { return []; }
    }

    public static string[] ParseStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static string? AsText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => string.Join(", ", a.Select(AsText)),
        _ => node.ToJsonString(),
    };
}
