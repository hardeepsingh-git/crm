using System.ComponentModel.DataAnnotations;
using Crm.Api.Auth;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Users;

public record NamedRef(Guid Id, string Name);

public record UserLookup(Guid Id, string FullName, string Email);

public record UserItem(
    Guid Id, string Email, string FirstName, string LastName, string FullName, bool IsActive, bool IsPlatformAdmin,
    IReadOnlyList<NamedRef> Roles, IReadOnlyList<NamedRef> Groups, DateTime CreatedAt, DateTimeOffset? LockoutEnd);

public record CreateUserRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(100)] string FirstName,
    [Required, MaxLength(100)] string LastName,
    [Required, MinLength(10)] string Password,
    Guid[] RoleIds,
    Guid[]? GroupIds = null);

public record UpdateUserRequest(
    [Required, MaxLength(100)] string FirstName,
    [Required, MaxLength(100)] string LastName,
    bool IsActive,
    Guid[] RoleIds,
    Guid[]? GroupIds = null);

public record ResetPasswordRequest([Required, MinLength(10)] string NewPassword);

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUsers(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/users").WithTags("Users");
        g.MapGet("/lookup", Lookup);
        var admin = g.MapGroup("").RequirePermission(AdminResources.Users, PermissionActions.Manage);
        admin.MapGet("/", List);
        admin.MapPost("/", Create);
        admin.MapPut("/{id:guid}", Update);
        admin.MapPost("/{id:guid}/reset-password", ResetPassword);
        return api;
    }

    private static async Task<Ok<List<UserLookup>>> Lookup(Access access, CrmDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Users.AsNoTracking()
            .Where(u => u.TenantId == access.TenantId && u.IsActive)
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Select(u => new UserLookup(u.Id, u.FirstName + " " + u.LastName, u.Email!))
            .ToListAsync(ct));

    private static async Task<Ok<List<UserItem>>> List(Access access, CrmDbContext db, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().Where(u => u.TenantId == access.TenantId)
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName).ToListAsync(ct);
        var ids = users.Select(u => u.Id).ToList();
        var roles = await db.UserRoles.Where(r => ids.Contains(r.UserId)).Select(r => new { r.UserId, r.RoleId, r.Role!.Name }).ToListAsync(ct);
        var groups = await db.GroupMembers.Where(m => ids.Contains(m.UserId))
            .Join(db.Groups, m => m.GroupId, g => g.Id, (m, g) => new { m.UserId, g.Id, g.Name }).ToListAsync(ct);

        return TypedResults.Ok(users.Select(u => ToItem(u,
            roles.Where(r => r.UserId == u.Id).Select(r => new NamedRef(r.RoleId, r.Name)).ToList(),
            groups.Where(g => g.UserId == u.Id).Select(g => new NamedRef(g.Id, g.Name)).ToList())).ToList());
    }

    private static async Task<Results<Created<UserItem>, ValidationProblem>> Create(
        CreateUserRequest req, Access access, CrmDbContext db, UserManager<AppUser> users, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        if (await ValidateAssignmentsAsync(req.RoleIds, req.GroupIds ?? [], p, db, ct) is { } problem) return problem;

        var user = new AppUser
        {
            UserName = req.Email.Trim(), Email = req.Email.Trim(), EmailConfirmed = true, TenantId = access.TenantId,
            FirstName = req.FirstName.Trim(), LastName = req.LastName.Trim(),
        };
        var result = await users.CreateAsync(user, req.Password);
        if (!result.Succeeded) return result.ToValidationProblem();

        await SetAssignmentsAsync(db, user.Id, req.RoleIds, req.GroupIds ?? [], ct);
        PermissionService.Invalidate(access.TenantId);
        return TypedResults.Created($"/api/users/{user.Id}", await LoadItemAsync(db, user, ct));
    }

    private static async Task<Results<Ok<UserItem>, NotFound, ValidationProblem>> Update(
        Guid id, UpdateUserRequest req, Access access, CrmDbContext db, UserManager<AppUser> users, TokenService tokens, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id && u.TenantId == access.TenantId, ct);
        if (user is null) return TypedResults.NotFound();
        if (await ValidateAssignmentsAsync(req.RoleIds, req.GroupIds ?? [], p, db, ct) is { } problem) return problem;

        if (id == access.UserId)
        {
            var keepsAdmin = await db.Roles.AnyAsync(r => req.RoleIds.Contains(r.Id) && r.IsAdministrator, ct);
            if (!req.IsActive || (p.IsAdministrator && !keepsAdmin))
                return Errors.Validation("isActive", "You cannot deactivate yourself or remove your own Administrator role.");
        }

        // Removing someone's Administrator role requires being an administrator yourself.
        if (!p.IsAdministrator && await db.UserRoles.AnyAsync(r => r.UserId == id && r.Role!.IsAdministrator && !req.RoleIds.Contains(r.RoleId), ct))
            return Errors.Validation("roleIds", "Only administrators can change another administrator's roles.");

        user.FirstName = req.FirstName.Trim();
        user.LastName = req.LastName.Trim();
        user.IsActive = req.IsActive;
        var update = await users.UpdateAsync(user);
        if (!update.Succeeded) return update.ToValidationProblem();

        await SetAssignmentsAsync(db, id, req.RoleIds, req.GroupIds ?? [], ct);
        if (!user.IsActive) await tokens.RevokeAllAsync(user.Id, ct);
        PermissionService.Invalidate(access.TenantId);
        return TypedResults.Ok(await LoadItemAsync(db, user, ct));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> ResetPassword(
        Guid id, ResetPasswordRequest req, Access access, CrmDbContext db, UserManager<AppUser> users, TokenService tokens, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id && u.TenantId == access.TenantId, ct);
        if (user is null) return TypedResults.NotFound();
        if (!p.IsAdministrator && await db.UserRoles.AnyAsync(r => r.UserId == id && r.Role!.IsAdministrator, ct))
            return Errors.Validation("password", "Only administrators can reset an administrator's password.");

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, req.NewPassword);
        if (!result.Succeeded) return result.ToValidationProblem();

        await users.SetLockoutEndDateAsync(user, null);
        await tokens.RevokeAllAsync(user.Id, ct);
        return TypedResults.NoContent();
    }

    /// <summary>Roles and groups must belong to the tenant, and the caller may only hand out permissions they hold.</summary>
    public static async Task<ValidationProblem?> ValidateAssignmentsAsync(Guid[] roleIds, Guid[] groupIds, EffectivePermissions p,
        CrmDbContext db, CancellationToken ct)
    {
        var roles = await db.Roles.Include(r => r.Permissions).Where(r => roleIds.Contains(r.Id)).ToListAsync(ct);
        if (roles.Count != roleIds.Distinct().Count()) return Errors.Validation("roleIds", "One or more roles do not exist.");
        if (await db.Groups.CountAsync(g => groupIds.Contains(g.Id), ct) != groupIds.Distinct().Count())
            return Errors.Validation("groupIds", "One or more groups do not exist.");

        var groupRoleIds = await db.GroupRoles.Where(g => groupIds.Contains(g.GroupId)).Select(g => g.RoleId).ToListAsync(ct);
        var groupRoles = await db.Roles.Include(r => r.Permissions).Where(r => groupRoleIds.Contains(r.Id)).ToListAsync(ct);
        var granted = roles.Concat(groupRoles).ToList();
        if (!p.IsAdministrator && granted.Any(r => r.IsAdministrator))
            return Errors.Validation("roleIds", "Only administrators can grant the Administrator role.");
        if (!p.Covers(granted.Select(r => new RoleGrants(r.Permissions.Select(x => new GrantDto(x.Resource, x.Action, x.Scope))))))
            return Errors.Validation("roleIds", "You can't grant permissions you don't have yourself.");
        return null;
    }

    private static async Task SetAssignmentsAsync(CrmDbContext db, Guid userId, Guid[] roleIds, Guid[] groupIds, CancellationToken ct)
    {
        db.UserRoles.RemoveRange(await db.UserRoles.Where(r => r.UserId == userId).ToListAsync(ct));
        db.UserRoles.AddRange(roleIds.Distinct().Select(r => new UserRole { UserId = userId, RoleId = r }));
        db.GroupMembers.RemoveRange(await db.GroupMembers.Where(m => m.UserId == userId).ToListAsync(ct));
        db.GroupMembers.AddRange(groupIds.Distinct().Select(g => new GroupMember { UserId = userId, GroupId = g }));
        await db.SaveChangesAsync(ct);
    }

    private static async Task<UserItem> LoadItemAsync(CrmDbContext db, AppUser user, CancellationToken ct)
    {
        var roles = await db.UserRoles.Where(r => r.UserId == user.Id).Select(r => new NamedRef(r.RoleId, r.Role!.Name)).ToListAsync(ct);
        var groups = await db.GroupMembers.Where(m => m.UserId == user.Id)
            .Join(db.Groups, m => m.GroupId, g => g.Id, (m, g) => new NamedRef(g.Id, g.Name)).ToListAsync(ct);
        return ToItem(user, roles, groups);
    }

    private static UserItem ToItem(AppUser u, IReadOnlyList<NamedRef> roles, IReadOnlyList<NamedRef> groups) =>
        new(u.Id, u.Email!, u.FirstName, u.LastName, u.FullName, u.IsActive, u.IsPlatformAdmin, roles, groups, u.CreatedAt, u.LockoutEnd);
}
