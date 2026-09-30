using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Crm.Api.Common;
using Crm.Api.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

namespace Crm.Api.Auth;

public record LoginRequest([Required, EmailAddress] string Email, [Required] string Password);

public record RefreshRequest([Required] string RefreshToken);

public record ChangePasswordRequest([Required] string CurrentPassword, [Required, MinLength(10)] string NewPassword);

public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";
    public const string RefreshRateLimitPolicy = "auth-refresh";

    public static RouteGroupBuilder MapAuth(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth").WithTags("Auth");
        g.MapPost("/login", Login).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        g.MapPost("/refresh", Refresh).AllowAnonymous().RequireRateLimiting(RefreshRateLimitPolicy);
        g.MapPost("/logout", Logout).AllowAnonymous();
        g.MapGet("/me", Me).RequireAuthorization();
        g.MapPost("/change-password", ChangePassword).RequireAuthorization();
        return api;
    }

    private static async Task<Results<Ok<AuthResponse>, ProblemHttpResult>> Login(
        LoginRequest req, UserManager<AppUser> users, TokenService tokens, CancellationToken ct)
    {
        var invalid = TypedResults.Problem("Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized);
        var user = await users.FindByEmailAsync(req.Email);
        if (user is null || !user.IsActive) return invalid;

        if (await users.IsLockedOutAsync(user))
            return TypedResults.Problem("Account is temporarily locked. Try again later.", statusCode: StatusCodes.Status423Locked);

        if (!await users.CheckPasswordAsync(user, req.Password))
        {
            await users.AccessFailedAsync(user);
            return invalid;
        }

        await users.ResetAccessFailedCountAsync(user);
        return TypedResults.Ok(await tokens.IssueAsync(user, ct));
    }

    private static async Task<Results<Ok<AuthResponse>, ProblemHttpResult>> Refresh(RefreshRequest req, TokenService tokens, CancellationToken ct)
    {
        var result = await tokens.RefreshAsync(req.RefreshToken, ct);
        return result is null
            ? TypedResults.Problem("Invalid or expired refresh token.", statusCode: StatusCodes.Status401Unauthorized)
            : TypedResults.Ok(result);
    }

    private static async Task<NoContent> Logout(RefreshRequest req, TokenService tokens, CancellationToken ct)
    {
        await tokens.RevokeAsync(req.RefreshToken, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<UserInfo>, NotFound>> Me(ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        var user = await users.FindByIdAsync(principal.GetUserId().ToString());
        if (user is null) return TypedResults.NotFound();
        var roles = (await users.GetRolesAsync(user)).ToList();
        return TypedResults.Ok(TokenService.ToUserInfo(user, roles));
    }

    private static async Task<Results<NoContent, ValidationProblem, NotFound>> ChangePassword(
        ChangePasswordRequest req, ClaimsPrincipal principal, UserManager<AppUser> users, TokenService tokens, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(principal.GetUserId().ToString());
        if (user is null) return TypedResults.NotFound();

        var result = await users.ChangePasswordAsync(user, req.CurrentPassword, req.NewPassword);
        if (!result.Succeeded) return result.ToValidationProblem();

        await tokens.RevokeAllAsync(user.Id, ct);
        return TypedResults.NoContent();
    }

    public static ValidationProblem ToValidationProblem(this IdentityResult result) =>
        TypedResults.ValidationProblem(result.Errors
            .GroupBy(e => e.Code.Contains("Password") ? "password" : e.Code.Contains("Email") || e.Code.Contains("UserName") ? "email" : "general")
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
}
