using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Crm.Api.Auth;
using Crm.Api.Common;
using Crm.Api.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Users;

public record UserLookup(Guid Id, string FullName, string Email);

public record UserItem(
    Guid Id, string Email, string FirstName, string LastName, string FullName, bool IsActive,
    IReadOnlyList<string> Roles, DateTime CreatedAt, DateTimeOffset? LockoutEnd);

public record CreateUserRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(100)] string FirstName,
    [Required, MaxLength(100)] string LastName,
    [Required, MinLength(10)] string Password,
    [Required, MinLength(1)] string[] Roles);

public record UpdateUserRequest(
    [Required, MaxLength(100)] string FirstName,
    [Required, MaxLength(100)] string LastName,
    bool IsActive,
    [Required, MinLength(1)] string[] Roles);

public record ResetPasswordRequest([Required, MinLength(10)] string NewPassword);

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUsers(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/users").WithTags("Users");
        g.MapGet("/lookup", Lookup);
        var admin = g.MapGroup("").RequireAuthorization(Policies.AdminOnly);
        admin.MapGet("/", List);
        admin.MapPost("/", Create);
        admin.MapPut("/{id:guid}", Update);
        admin.MapPost("/{id:guid}/reset-password", ResetPassword);
        return api;
    }

    private static async Task<Ok<List<UserLookup>>> Lookup(UserManager<AppUser> users, CancellationToken ct) =>
        TypedResults.Ok(await users.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Select(u => new UserLookup(u.Id, u.FirstName + " " + u.LastName, u.Email!))
            .ToListAsync(ct));

    private static async Task<Ok<List<UserItem>>> List(UserManager<AppUser> users, CancellationToken ct)
    {
        var all = await users.Users.AsNoTracking().OrderBy(u => u.FirstName).ThenBy(u => u.LastName).ToListAsync(ct);
        var result = new List<UserItem>(all.Count);
        foreach (var u in all) result.Add(ToItem(u, (await users.GetRolesAsync(u)).ToList()));
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Created<UserItem>, ValidationProblem>> Create(CreateUserRequest req, UserManager<AppUser> users)
    {
        if (InvalidRoles(req.Roles) is { } roleError) return roleError;

        var user = new AppUser
        {
            UserName = req.Email.Trim(),
            Email = req.Email.Trim(),
            EmailConfirmed = true,
            FirstName = req.FirstName.Trim(),
            LastName = req.LastName.Trim(),
        };
        var result = await users.CreateAsync(user, req.Password);
        if (!result.Succeeded) return result.ToValidationProblem();

        await users.AddToRolesAsync(user, req.Roles.Distinct());
        return TypedResults.Created($"/api/users/{user.Id}", ToItem(user, req.Roles.Distinct().ToList()));
    }

    private static async Task<Results<Ok<UserItem>, NotFound, ValidationProblem>> Update(
        Guid id, UpdateUserRequest req, ClaimsPrincipal principal, UserManager<AppUser> users, TokenService tokens, CancellationToken ct)
    {
        if (InvalidRoles(req.Roles) is { } roleError) return roleError;
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return TypedResults.NotFound();

        var isSelf = principal.GetUserId() == id;
        if (isSelf && (!req.IsActive || !req.Roles.Contains(Roles.Admin)))
            return Errors.Validation("isActive", "You cannot deactivate yourself or remove your own Admin role.");

        user.FirstName = req.FirstName.Trim();
        user.LastName = req.LastName.Trim();
        user.IsActive = req.IsActive;
        var update = await users.UpdateAsync(user);
        if (!update.Succeeded) return update.ToValidationProblem();

        var current = await users.GetRolesAsync(user);
        await users.RemoveFromRolesAsync(user, current.Except(req.Roles));
        await users.AddToRolesAsync(user, req.Roles.Distinct().Except(current));

        if (!user.IsActive) await tokens.RevokeAllAsync(user.Id, ct);
        return TypedResults.Ok(ToItem(user, (await users.GetRolesAsync(user)).ToList()));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> ResetPassword(
        Guid id, ResetPasswordRequest req, UserManager<AppUser> users, TokenService tokens, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return TypedResults.NotFound();

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, req.NewPassword);
        if (!result.Succeeded) return result.ToValidationProblem();

        await users.SetLockoutEndDateAsync(user, null);
        await tokens.RevokeAllAsync(user.Id, ct);
        return TypedResults.NoContent();
    }

    private static ValidationProblem? InvalidRoles(string[] roles) =>
        roles.All(r => Roles.All.Contains(r))
            ? null
            : Errors.Validation("roles", $"Roles must be any of: {string.Join(", ", Roles.All)}.");

    private static UserItem ToItem(AppUser u, IReadOnlyList<string> roles) =>
        new(u.Id, u.Email!, u.FirstName, u.LastName, u.FullName, u.IsActive, roles, u.CreatedAt, u.LockoutEnd);
}
