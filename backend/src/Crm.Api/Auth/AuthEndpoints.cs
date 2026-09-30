using System.ComponentModel.DataAnnotations;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

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
        LoginRequest req, UserManager<AppUser> users, CrmDbContext db, TokenService tokens, RegistrationService registration, CancellationToken ct)
    {
        var invalid = TypedResults.Problem("Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized);
        var user = await users.FindByEmailAsync(req.Email);
        if (user is null) return invalid;

        if (await users.IsLockedOutAsync(user))
            return TypedResults.Problem("Account is temporarily locked. Try again later.", statusCode: StatusCodes.Status423Locked);

        if (!await users.CheckPasswordAsync(user, req.Password))
        {
            await users.AccessFailedAsync(user);
            return invalid;
        }

        // Registration state is only revealed to someone who knows the password.
        if (user.RegistrationStatus != RegistrationStatus.Approved)
        {
            var (status, remaining) = await registration.CheckPendingAsync(user, ct);
            var detail = status switch
            {
                RegistrationStatus.PendingVerification => "Please verify your " +
                    string.Join(" and ", remaining.Select(c => c == VerificationChannel.Email ? "email address" : "mobile number")) + " to continue.",
                RegistrationStatus.PendingApproval => "Your registration is awaiting administrator approval.",
                _ => "Your registration was not approved. Contact your organisation's administrator.",
            };
            return TypedResults.Problem(detail, statusCode: StatusCodes.Status403Forbidden, extensions: new Dictionary<string, object?>
            {
                ["registrationStatus"] = status.ToString(),
                ["verification"] = remaining.Select(c => c.ToString()).ToArray(),
            });
        }
        if (!user.IsActive) return invalid;

        var tenantStatus = await db.Tenants.Where(t => t.Id == user.TenantId).Select(t => t.Status).SingleAsync(ct);
        if (tenantStatus != TenantStatus.Active)
            return TypedResults.Problem("Your organisation's account is suspended. Contact your administrator.", statusCode: StatusCodes.Status403Forbidden);

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

    private static async Task<Results<Ok<UserInfo>, NotFound>> Me(Access access, TokenService tokens, CancellationToken ct)
    {
        var info = await tokens.DescribeAsync(access.UserId, ct);
        return info is null ? TypedResults.NotFound() : TypedResults.Ok(info);
    }

    private static async Task<Results<NoContent, ValidationProblem, NotFound>> ChangePassword(
        ChangePasswordRequest req, Access access, UserManager<AppUser> users, TokenService tokens, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(access.UserId.ToString());
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
