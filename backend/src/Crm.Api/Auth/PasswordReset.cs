using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Messaging;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Auth;

public record ForgotPasswordRequest([Required, EmailAddress, MaxLength(256)] string Email);

public record ResetPasswordWithTokenRequest([Required, MaxLength(128)] string Token, [Required] string NewPassword);

/// <summary>"Forgot password" by emailed link. Responses never reveal whether an account exists.</summary>
public sealed class PasswordResetService(
    CrmDbContext db, UserManager<AppUser> users, TokenService tokens, IEmailSender email, IConfiguration config,
    IHostEnvironment env, TimeProvider clock, ILogger<PasswordResetService> logger)
{
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);

    public async Task RequestAsync(ForgotPasswordRequest req, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(req.Email.Trim());
        if (user is null || !user.IsActive || user.RegistrationStatus != RegistrationStatus.Approved) return;
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == user.TenantId, ct);
        if (tenant.Status != TenantStatus.Active) return;

        if (PublicUrl() is not { } baseUrl)
        {
            logger.LogError("Password reset requested but App:PublicUrl is not configured, so no link can be sent");
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var open = await db.PasswordResetTokens.Where(t => t.UserId == user.Id && t.ConsumedAt == null).ToListAsync(ct);
        if (open.Any(t => t.CreatedAt + ResendCooldown > now)) return;
        foreach (var t in open) t.ConsumedAt = now;

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var minutes = config.GetValue("PasswordReset:TokenMinutes", 60);
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id, TokenHash = Hash(token), CreatedAt = now, ExpiresAt = now.AddMinutes(minutes),
        });
        await db.SaveChangesAsync(ct);

        try
        {
            await email.SendAsync(user.Email!, $"Reset your {tenant.Name} password",
                $"Hi {user.FirstName},\n\nUse this link to choose a new password. It expires in {minutes} minutes and can be used once:\n\n" +
                $"{baseUrl}/reset-password?token={token}\n\nIf you didn't ask for this, ignore this email; your password stays the same.", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to send password reset email to user {UserId}", user.Id);
        }
    }

    public async Task<Results<NoContent, ValidationProblem>> ResetAsync(ResetPasswordWithTokenRequest req, CancellationToken ct)
    {
        var invalid = Errors.Validation("token", "This reset link is invalid or has expired. Request a new one.");
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = Hash(req.Token.Trim());
        var stored = await db.PasswordResetTokens.Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == hash && t.ConsumedAt == null, ct);
        if (stored?.User is not { IsActive: true, RegistrationStatus: RegistrationStatus.Approved } user || stored.ExpiresAt <= now) return invalid;

        var identityToken = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, identityToken, req.NewPassword);
        if (!result.Succeeded) return result.ToValidationProblem();

        await db.PasswordResetTokens.Where(t => t.UserId == user.Id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, now), ct);
        // Receiving the link proves the mailbox; clearing the lockout lets a locked-out user back in.
        user.EmailConfirmed = true;
        await users.UpdateAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await tokens.RevokeAllAsync(user.Id, ct);
        logger.LogInformation("Password reset completed for user {UserId}", user.Id);

        try
        {
            await email.SendAsync(user.Email!, "Your password was changed",
                $"Hi {user.FirstName},\n\nYour password was just reset and you were signed out everywhere.\n\n" +
                "If this wasn't you, contact your administrator immediately.", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to send password-changed notice to user {UserId}", user.Id);
        }
        return TypedResults.NoContent();
    }

    /// <summary>Links must use a configured address: deriving it from the request Host header would allow link poisoning.</summary>
    private string? PublicUrl()
    {
        var configured = config["App:PublicUrl"];
        if (string.IsNullOrWhiteSpace(configured) && env.IsDevelopment()) configured = "http://localhost:4200";
        return string.IsNullOrWhiteSpace(configured) ? null : configured.TrimEnd('/');
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public static class PasswordResetEndpoints
{
    public const string RateLimitPolicy = "password-reset";

    public static RouteGroupBuilder MapPasswordReset(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth").WithTags("Auth").AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        g.MapPost("/forgot-password", async (ForgotPasswordRequest req, PasswordResetService svc, CancellationToken ct) =>
        {
            await svc.RequestAsync(req, ct);
            return TypedResults.Accepted((string?)null);
        });
        g.MapPost("/reset-password", (ResetPasswordWithTokenRequest req, PasswordResetService svc, CancellationToken ct) => svc.ResetAsync(req, ct));
        return api;
    }
}
