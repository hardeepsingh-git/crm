using System.Collections.Concurrent;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Crm.Api.Security;

public record GrantDto(string Resource, string Action, PermissionScope Scope);

/// <summary>
/// The grants of one role. An explicit grant for an object (including <see cref="PermissionScope.None"/>, i.e. deny)
/// overrides the role's "*" default for that object and action; "*" never applies to admin areas.
/// </summary>
public sealed class RoleGrants
{
    private readonly Dictionary<(string Resource, string Action), PermissionScope> _grants;

    public RoleGrants(IEnumerable<GrantDto> grants) =>
        _grants = grants.GroupBy(g => (g.Resource, g.Action)).ToDictionary(g => g.Key, g => g.Max(x => x.Scope));

    public IEnumerable<(string Resource, string Action)> Keys => _grants.Keys;

    public PermissionScope Scope(string resource, string action)
    {
        if (_grants.TryGetValue((resource, action), out var explicitScope)) return explicitScope;
        return EffectivePermissions.IsAdminResource(resource) ? PermissionScope.None : _grants.GetValueOrDefault(("*", action));
    }
}

/// <summary>The resolved permissions of one user: the union (widest scope) of every role assigned directly or through groups.</summary>
public sealed class EffectivePermissions
{
    private readonly IReadOnlyList<RoleGrants> _roles;

    public EffectivePermissions(
        Guid userId, Guid tenantId, bool tenantActive, bool isAdministrator, bool isPlatformAdmin,
        IReadOnlySet<Guid> teamUserIds, IEnumerable<RoleGrants> roles, IReadOnlyList<string> roleNames)
    {
        UserId = userId;
        TenantId = tenantId;
        TenantActive = tenantActive;
        IsAdministrator = isAdministrator;
        IsPlatformAdmin = isPlatformAdmin;
        TeamUserIds = teamUserIds;
        RoleNames = roleNames;
        _roles = roles.ToList();
    }

    public Guid UserId { get; }
    public Guid TenantId { get; }
    public bool TenantActive { get; }
    public bool IsAdministrator { get; }
    public bool IsPlatformAdmin { get; }
    /// <summary>The user plus every member of every group the user belongs to.</summary>
    public IReadOnlySet<Guid> TeamUserIds { get; }
    public IReadOnlyList<string> RoleNames { get; }

    public static bool IsAdminResource(string resource) => AdminResources.All.Contains(resource);

    public PermissionScope Scope(string resource, string action)
    {
        if (!TenantActive) return PermissionScope.None;
        if (IsAdministrator) return PermissionScope.All;
        return Widest(_roles, resource, action);
    }

    private static PermissionScope Widest(IEnumerable<RoleGrants> roles, string resource, string action) =>
        roles.Select(r => r.Scope(resource, action)).DefaultIfEmpty(PermissionScope.None).Max();

    public bool Can(string resource, string action) => Scope(resource, action) != PermissionScope.None;

    public bool CanAccess(string resource, string action, Guid ownerId) => Scope(resource, action) switch
    {
        PermissionScope.All => true,
        PermissionScope.Group => TeamUserIds.Contains(ownerId),
        PermissionScope.Own => ownerId == UserId,
        _ => false,
    };

    /// <summary>Restricts a query to the records the user may see (or act on) for the given action.</summary>
    public IQueryable<T> Filter<T>(IQueryable<T> query, string resource, string action = PermissionActions.Read) where T : OwnedEntity
    {
        var me = UserId;
        var team = TeamUserIds.ToList();
        return Scope(resource, action) switch
        {
            PermissionScope.All => query,
            PermissionScope.Group => query.Where(x => team.Contains(x.OwnerId)),
            PermissionScope.Own => query.Where(x => x.OwnerId == me),
            _ => query.Where(_ => false),
        };
    }

    /// <summary>
    /// Anti-escalation: a non-administrator may only hand out roles whose resolved access never exceeds their own.
    /// Resources not mentioned by either side resolve through "*" on both, so checking "*" plus every explicitly
    /// mentioned resource is exhaustive.
    /// </summary>
    public bool Covers(IEnumerable<RoleGrants> roles)
    {
        if (IsAdministrator) return true;
        var granted = roles.ToList();
        var keys = granted.SelectMany(r => r.Keys).Concat(_roles.SelectMany(r => r.Keys)).ToList();
        var resources = keys.Select(k => k.Resource).Append("*").Distinct();
        var actions = keys.Select(k => k.Action).Concat(PermissionActions.Record).Distinct().ToList();
        return resources.All(resource => actions.All(action => Widest(granted, resource, action) <= Scope(resource, action)));
    }

    /// <summary>
    /// Resolved scopes for the UI: "*" plus every resource any role mentions. Resources missing from the map resolve
    /// through "*" (business objects) or to None (admin areas).
    /// </summary>
    public Dictionary<string, Dictionary<string, PermissionScope>> ToDictionary()
    {
        var keys = _roles.SelectMany(r => r.Keys).ToList();
        var wildcardActions = keys.Where(k => k.Resource == "*").Select(k => k.Action);
        return keys.Select(k => k.Resource).Append("*").Distinct().ToDictionary(
            resource => resource,
            resource => keys.Where(k => k.Resource == resource).Select(k => k.Action)
                .Concat(IsAdminResource(resource) ? [] : wildcardActions)
                .Distinct()
                .ToDictionary(action => action, action => Scope(resource, action)));
    }
}

/// <summary>Loads and caches effective permissions. Changes to roles, groups or memberships invalidate the tenant's cache.</summary>
public sealed class PermissionService(CrmDbContext db, IMemoryCache cache)
{
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> TenantTokens = new();

    public static void Invalidate(Guid tenantId)
    {
        if (TenantTokens.TryRemove(tenantId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public async Task<EffectivePermissions?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        if (cache.TryGetValue(("perm", userId), out EffectivePermissions? cached)) return cached;

        // Explicit tenant scoping: this also runs at sign-in, before a tenant context exists.
        var user = await db.Users.AsNoTracking().Include(u => u.Tenant).SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user?.Tenant is null) return null;
        var tenantId = user.TenantId;

        var groupIds = await db.GroupMembers.Where(m => m.UserId == userId).Select(m => m.GroupId).ToListAsync(ct);
        var roleIds = await db.UserRoles.Where(r => r.UserId == userId).Select(r => r.RoleId)
            .Union(db.GroupRoles.Where(g => groupIds.Contains(g.GroupId)).Select(g => g.RoleId))
            .ToListAsync(ct);
        var roles = await db.Roles.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TenantId == tenantId && roleIds.Contains(r.Id))
            .Include(r => r.Permissions)
            .ToListAsync(ct);
        var team = await db.GroupMembers.Where(m => groupIds.Contains(m.GroupId)).Select(m => m.UserId).Distinct().ToListAsync(ct);
        team.Add(userId);

        var permissions = new EffectivePermissions(
            userId, tenantId,
            user.Tenant.Status == TenantStatus.Active && user.IsActive,
            roles.Any(r => r.IsAdministrator),
            user.IsPlatformAdmin,
            team.ToHashSet(),
            roles.Select(r => new RoleGrants(r.Permissions.Select(p => new GrantDto(p.Resource, p.Action, p.Scope)))),
            roles.Select(r => r.Name).OrderBy(n => n).ToList());

        var token = TenantTokens.GetOrAdd(tenantId, _ => new CancellationTokenSource());
        cache.Set(("perm", userId), permissions, new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(5))
            .AddExpirationToken(new CancellationChangeToken(token.Token)));
        return permissions;
    }
}

/// <summary>Per-request access to the caller's identity and permissions.</summary>
public sealed class Access(PermissionService service, TenantContext tenant)
{
    private EffectivePermissions? _permissions;

    public Guid UserId => tenant.UserId ?? throw new InvalidOperationException("No authenticated user.");
    public Guid TenantId => tenant.TenantId ?? throw new InvalidOperationException("No tenant context.");

    public async ValueTask<EffectivePermissions> GetAsync(CancellationToken ct = default) =>
        _permissions ??= await service.GetAsync(UserId, ct)
            ?? throw new UnauthorizedAccessException("User no longer exists.");
}

public static class PermissionEndpointExtensions
{
    /// <summary>Returns 403 unless the caller holds <paramref name="action"/> on the resource at any scope.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string resource, string action)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequirePermission(_ => resource, action);

    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, Func<HttpContext, string> resource, string action)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var access = ctx.HttpContext.RequestServices.GetRequiredService<Access>();
            var permissions = await access.GetAsync(ctx.HttpContext.RequestAborted);
            return permissions.Can(resource(ctx.HttpContext), action) ? await next(ctx) : Results.Forbid();
        });
        return builder;
    }

    public static TBuilder RequireAnyPermission<TBuilder>(this TBuilder builder, params (string Resource, string Action)[] anyOf)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var permissions = await ctx.HttpContext.RequestServices.GetRequiredService<Access>().GetAsync(ctx.HttpContext.RequestAborted);
            return anyOf.Any(x => permissions.Can(x.Resource, x.Action)) ? await next(ctx) : Results.Forbid();
        });
        return builder;
    }

    public static TBuilder RequirePlatformAdmin<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var permissions = await ctx.HttpContext.RequestServices.GetRequiredService<Access>().GetAsync(ctx.HttpContext.RequestAborted);
            return permissions.IsPlatformAdmin && permissions.TenantActive ? await next(ctx) : Results.Forbid();
        });
        return builder;
    }
}

public static class Ownership
{
    /// <summary>Owners may be assigned within the caller's update scope: self (Own), team (Group) or anyone active (All).</summary>
    public static async Task<string?> ValidateAsync(
        Guid? requested, Guid currentOwner, string resource, EffectivePermissions permissions, CrmDbContext db, CancellationToken ct)
    {
        if (requested is null || requested == currentOwner) return null;
        var allowed = permissions.Scope(resource, PermissionActions.Update) switch
        {
            PermissionScope.All => true,
            PermissionScope.Group => permissions.TeamUserIds.Contains(requested.Value),
            _ => requested == permissions.UserId,
        };
        if (!allowed) return "You can't assign records to that user.";
        var exists = await db.Users.AnyAsync(u => u.Id == requested && u.TenantId == permissions.TenantId && u.IsActive, ct);
        return exists ? null : "Owner does not exist or is inactive.";
    }
}
