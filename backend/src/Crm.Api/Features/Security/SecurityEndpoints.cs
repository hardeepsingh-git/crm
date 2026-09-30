using System.ComponentModel.DataAnnotations;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Features.Users;
using Crm.Api.Metadata;
using Crm.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Security;

public record RoleItem(
    Guid Id, string Name, string? Description, bool IsSystem, bool IsAdministrator,
    IReadOnlyList<GrantDto> Permissions, int UserCount, int GroupCount);

public record RoleRequest([Required, MaxLength(100)] string Name, [MaxLength(500)] string? Description, GrantDto[] Permissions);

public record GroupItem(Guid Id, string Name, string? Description, IReadOnlyList<NamedRef> Members, IReadOnlyList<NamedRef> Roles);

public record GroupRequest([Required, MaxLength(100)] string Name, [MaxLength(500)] string? Description, Guid[] MemberIds, Guid[] RoleIds);

public static class SecurityEndpoints
{
    private static readonly (string, string)[] SecurityAdmins =
    [
        (AdminResources.Users, PermissionActions.Manage),
        (AdminResources.Roles, PermissionActions.Manage),
        (AdminResources.Groups, PermissionActions.Manage),
    ];

    public static RouteGroupBuilder MapSecurity(this RouteGroupBuilder api)
    {
        var roles = api.MapGroup("/admin/roles").WithTags("Roles");
        roles.MapGet("/", ListRoles).RequireAnyPermission(SecurityAdmins);
        roles.MapPost("/", CreateRole).RequirePermission(AdminResources.Roles, PermissionActions.Manage);
        roles.MapPut("/{id:guid}", UpdateRole).RequirePermission(AdminResources.Roles, PermissionActions.Manage);
        roles.MapDelete("/{id:guid}", DeleteRole).RequirePermission(AdminResources.Roles, PermissionActions.Manage);

        var groups = api.MapGroup("/admin/groups").WithTags("Groups");
        groups.MapGet("/", ListGroups).RequireAnyPermission(SecurityAdmins);
        groups.MapPost("/", CreateGroup).RequirePermission(AdminResources.Groups, PermissionActions.Manage);
        groups.MapPut("/{id:guid}", UpdateGroup).RequirePermission(AdminResources.Groups, PermissionActions.Manage);
        groups.MapDelete("/{id:guid}", DeleteGroup).RequirePermission(AdminResources.Groups, PermissionActions.Manage);
        return api;
    }

    // ---------- Roles ----------

    private static async Task<Ok<List<RoleItem>>> ListRoles(CrmDbContext db, CancellationToken ct)
    {
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).OrderBy(r => r.Name).ToListAsync(ct);
        var ids = roles.Select(r => r.Id).ToList();
        var users = await db.UserRoles.Where(u => ids.Contains(u.RoleId)).GroupBy(u => u.RoleId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var groups = await db.GroupRoles.Where(u => ids.Contains(u.RoleId)).GroupBy(u => u.RoleId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return TypedResults.Ok(roles.Select(r => ToItem(r, users.GetValueOrDefault(r.Id), groups.GetValueOrDefault(r.Id))).ToList());
    }

    private static async Task<Results<Created<RoleItem>, ValidationProblem>> CreateRole(
        RoleRequest req, Access access, CrmDbContext db, MetadataService meta, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        if (await ValidateRoleAsync(req, null, p, db, meta, ct) is { } problem) return problem;
        var role = new Role { Name = req.Name.Trim(), Description = req.Description };
        role.Permissions.AddRange(Normalize(req.Permissions).Select(g => new RolePermission { Resource = g.Resource, Action = g.Action, Scope = g.Scope }));
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/admin/roles/{role.Id}", ToItem(role, 0, 0));
    }

    private static async Task<Results<Ok<RoleItem>, NotFound, ValidationProblem>> UpdateRole(
        Guid id, RoleRequest req, Access access, CrmDbContext db, MetadataService meta, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var role = await db.Roles.Include(r => r.Permissions).SingleOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return TypedResults.NotFound();
        if (role.IsAdministrator) return Errors.Validation("permissions", "The Administrator role always has full access and cannot be edited.");
        if (await ValidateRoleAsync(req, id, p, db, meta, ct) is { } problem) return problem;

        role.Name = req.Name.Trim();
        role.Description = req.Description;
        db.RolePermissions.RemoveRange(role.Permissions);
        db.RolePermissions.AddRange(Normalize(req.Permissions).Select(g => new RolePermission { RoleId = role.Id, Resource = g.Resource, Action = g.Action, Scope = g.Scope }));
        await db.SaveChangesAsync(ct);
        PermissionService.Invalidate(access.TenantId);
        await db.Entry(role).Collection(r => r.Permissions).LoadAsync(ct);
        return TypedResults.Ok(ToItem(role, await db.UserRoles.CountAsync(u => u.RoleId == id, ct), await db.GroupRoles.CountAsync(g => g.RoleId == id, ct)));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> DeleteRole(Guid id, Access access, CrmDbContext db, CancellationToken ct)
    {
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return TypedResults.NotFound();
        if (role.IsSystem) return Errors.Validation("role", "System roles cannot be deleted.");
        db.Roles.Remove(role);
        await db.SaveChangesAsync(ct);
        PermissionService.Invalidate(access.TenantId);
        return TypedResults.NoContent();
    }

    private static async Task<ValidationProblem?> ValidateRoleAsync(RoleRequest req, Guid? id, EffectivePermissions p, CrmDbContext db,
        MetadataService meta, CancellationToken ct)
    {
        var name = req.Name.Trim();
        if (await db.Roles.AnyAsync(r => r.Name == name && r.Id != id, ct)) return Errors.Validation("name", "A role with this name already exists.");

        var objectKeys = (await meta.ObjectsAsync(ct)).Select(o => o.Key).ToHashSet();
        foreach (var g in req.Permissions)
        {
            var isAdmin = AdminResources.All.Contains(g.Resource);
            var valid = isAdmin
                ? g.Action == (g.Resource == AdminResources.Audit ? PermissionActions.Read : PermissionActions.Manage)
                : (g.Resource == "*" || objectKeys.Contains(g.Resource)) && PermissionActions.Record.Contains(g.Action);
            if (!valid) return Errors.Validation("permissions", $"Invalid permission '{g.Resource}:{g.Action}'.");
        }
        if (!p.Covers([new RoleGrants(Normalize(req.Permissions))])) return Errors.Validation("permissions", "You can't grant permissions you don't have yourself.");
        return null;
    }

    /// <summary>
    /// Collapses duplicates to the widest scope and treats create/manage as all-or-nothing. "None" is kept for specific
    /// objects (an explicit deny that overrides "*") and dropped elsewhere, where it is the default anyway.
    /// </summary>
    private static IEnumerable<GrantDto> Normalize(IEnumerable<GrantDto> grants) =>
        grants.GroupBy(g => (g.Resource, g.Action))
            .Select(g => new GrantDto(g.Key.Resource, g.Key.Action, g.Max(x => x.Scope) switch
            {
                PermissionScope.None => PermissionScope.None,
                _ when g.Key.Action is PermissionActions.Create or PermissionActions.Manage || AdminResources.All.Contains(g.Key.Resource)
                    => PermissionScope.All,
                var widest => widest,
            }))
            .Where(g => g.Scope != PermissionScope.None || (g.Resource != "*" && !AdminResources.All.Contains(g.Resource)));

    private static RoleItem ToItem(Role r, int users, int groups) => new(
        r.Id, r.Name, r.Description, r.IsSystem, r.IsAdministrator,
        r.Permissions.Select(p => new GrantDto(p.Resource, p.Action, p.Scope)).OrderBy(p => p.Resource).ThenBy(p => p.Action).ToList(),
        users, groups);

    // ---------- Groups ----------

    private static async Task<Ok<List<GroupItem>>> ListGroups(CrmDbContext db, CancellationToken ct)
    {
        var groups = await db.Groups.AsNoTracking()
            .Include(g => g.Members).ThenInclude(m => m.User)
            .Include(g => g.Roles).ThenInclude(r => r.Role)
            .OrderBy(g => g.Name).ToListAsync(ct);
        return TypedResults.Ok(groups.Select(ToItem).ToList());
    }

    private static async Task<Results<Created<GroupItem>, ValidationProblem>> CreateGroup(
        GroupRequest req, Access access, CrmDbContext db, CancellationToken ct)
    {
        if (await ValidateGroupAsync(req, null, access, db, ct) is { } problem) return problem;
        var group = new Group { Name = req.Name.Trim(), Description = req.Description };
        group.Members.AddRange(req.MemberIds.Distinct().Select(u => new GroupMember { UserId = u }));
        group.Roles.AddRange(req.RoleIds.Distinct().Select(r => new GroupRole { RoleId = r }));
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);
        PermissionService.Invalidate(access.TenantId);
        return TypedResults.Created($"/api/admin/groups/{group.Id}", await LoadGroupAsync(db, group.Id, ct));
    }

    private static async Task<Results<Ok<GroupItem>, NotFound, ValidationProblem>> UpdateGroup(
        Guid id, GroupRequest req, Access access, CrmDbContext db, CancellationToken ct)
    {
        var group = await db.Groups.Include(g => g.Members).Include(g => g.Roles).SingleOrDefaultAsync(g => g.Id == id, ct);
        if (group is null) return TypedResults.NotFound();
        if (await ValidateGroupAsync(req, id, access, db, ct) is { } problem) return problem;

        group.Name = req.Name.Trim();
        group.Description = req.Description;
        db.GroupMembers.RemoveRange(group.Members);
        db.GroupRoles.RemoveRange(group.Roles);
        await db.SaveChangesAsync(ct);
        db.GroupMembers.AddRange(req.MemberIds.Distinct().Select(u => new GroupMember { GroupId = id, UserId = u }));
        db.GroupRoles.AddRange(req.RoleIds.Distinct().Select(r => new GroupRole { GroupId = id, RoleId = r }));
        await db.SaveChangesAsync(ct);
        PermissionService.Invalidate(access.TenantId);
        return TypedResults.Ok(await LoadGroupAsync(db, id, ct));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteGroup(Guid id, Access access, CrmDbContext db, CancellationToken ct)
    {
        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == id, ct);
        if (group is null) return TypedResults.NotFound();
        db.Groups.Remove(group);
        await db.SaveChangesAsync(ct);
        PermissionService.Invalidate(access.TenantId);
        return TypedResults.NoContent();
    }

    private static async Task<ValidationProblem?> ValidateGroupAsync(GroupRequest req, Guid? id, Access access, CrmDbContext db, CancellationToken ct)
    {
        var name = req.Name.Trim();
        if (await db.Groups.AnyAsync(g => g.Name == name && g.Id != id, ct)) return Errors.Validation("name", "A group with this name already exists.");
        var memberIds = req.MemberIds.Distinct().ToList();
        if (await db.Users.CountAsync(u => memberIds.Contains(u.Id) && u.TenantId == access.TenantId, ct) != memberIds.Count)
            return Errors.Validation("memberIds", "One or more users do not exist.");
        return await UserEndpoints.ValidateAssignmentsAsync(req.RoleIds, [], await access.GetAsync(ct), db, ct);
    }

    private static async Task<GroupItem> LoadGroupAsync(CrmDbContext db, Guid id, CancellationToken ct) =>
        ToItem(await db.Groups.AsNoTracking()
            .Include(g => g.Members).ThenInclude(m => m.User)
            .Include(g => g.Roles).ThenInclude(r => r.Role)
            .SingleAsync(g => g.Id == id, ct));

    private static GroupItem ToItem(Group g) => new(
        g.Id, g.Name, g.Description,
        g.Members.Select(m => new NamedRef(m.UserId, m.User?.FullName ?? "")).OrderBy(m => m.Name).ToList(),
        g.Roles.Select(r => new NamedRef(r.RoleId, r.Role?.Name ?? "")).OrderBy(r => r.Name).ToList());
}
