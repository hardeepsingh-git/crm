using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Messaging;
using Crm.Api.Platform;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Auth;

public record RegistrationOptions(bool Available, string? TenantName, bool EmailVerification, bool SmsVerification);

public record RegisterRequest(
    [Required, MaxLength(64)] string TenantCode,
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(100)] string FirstName,
    [Required, MaxLength(100)] string LastName,
    [MaxLength(32)] string? PhoneNumber,
    [Required] string Password);

public record RegisterResponse(IReadOnlyList<VerificationChannel> Verification, string Message);

public record VerifyRequest([Required, EmailAddress, MaxLength(256)] string Email, VerificationChannel Channel, [Required, MaxLength(10)] string Code);

public record VerifyResponse(RegistrationStatus Status, IReadOnlyList<VerificationChannel> Remaining);

public record ResendCodeRequest([Required, EmailAddress, MaxLength(256)] string Email, VerificationChannel Channel);

/// <summary>
/// Self-registration: users sign up for an organisation (by its code), verify the enabled channels, then wait for an
/// administrator to approve them. Registrations stay inactive with no roles until approved.
/// </summary>
public sealed partial class RegistrationService(
    CrmDbContext db, UserManager<AppUser> users, PlatformSettingsService settingsService, IEmailSender email, ISmsSender sms,
    TenantContext tenantContext, TimeProvider clock, ILogger<RegistrationService> logger)
{
    public const string ApprovalLink = "/settings/users?tab=pending";

    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    private static partial Regex E164();

    [GeneratedRegex(@"[\s\-().]")]
    private static partial Regex PhoneSeparators();

    /// <summary>Normalises to E.164 (e.g. "+61 412 345 678" becomes "+61412345678"); null when invalid.</summary>
    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var compact = PhoneSeparators().Replace(phone.Trim(), "");
        if (compact.StartsWith("00")) compact = "+" + compact[2..];
        return E164().IsMatch(compact) ? compact : null;
    }

    /// <summary>Channels the user still has to verify under the current settings.</summary>
    public static IReadOnlyList<VerificationChannel> Remaining(AppUser user, PlatformSettings s) =>
        [.. s.EmailVerificationEnabled && !user.EmailConfirmed ? [VerificationChannel.Email] : Array.Empty<VerificationChannel>(),
         .. s.SmsVerificationEnabled && !user.PhoneNumberConfirmed && user.PhoneNumber is not null ? [VerificationChannel.Sms] : Array.Empty<VerificationChannel>()];

    public async Task<RegistrationOptions> OptionsAsync(string? tenantCode, CancellationToken ct)
    {
        var (tenant, s) = await ResolveAsync(tenantCode, ct);
        // Without a code only the platform switch is disclosed, so the sign-in page knows whether to offer registration.
        if (string.IsNullOrWhiteSpace(tenantCode))
            return new RegistrationOptions(s.SelfRegistrationEnabled, null, s.EmailVerificationEnabled, s.SmsVerificationEnabled);
        return tenant is null
            ? new RegistrationOptions(false, null, false, false)
            : new RegistrationOptions(true, tenant.Name, s.EmailVerificationEnabled, s.SmsVerificationEnabled);
    }

    public async Task<Results<Accepted<RegisterResponse>, ProblemHttpResult, ValidationProblem>> RegisterAsync(RegisterRequest req, CancellationToken ct)
    {
        var (tenant, s) = await ResolveAsync(req.TenantCode, ct);
        if (tenant is null)
            return TypedResults.Problem("Self-registration is not available for this organisation.", statusCode: StatusCodes.Status404NotFound);

        var channels = s.RequiredChannels();
        var errors = new Dictionary<string, string[]>();
        var phone = NormalizePhone(req.PhoneNumber);
        if (!string.IsNullOrWhiteSpace(req.PhoneNumber) && phone is null)
            errors["phoneNumber"] = ["Enter the mobile number in international format, e.g. +61412345678."];
        else if (s.SmsVerificationEnabled && phone is null)
            errors["phoneNumber"] = ["A mobile number is required."];

        var user = new AppUser
        {
            UserName = req.Email.Trim(), Email = req.Email.Trim(), TenantId = tenant.Id,
            FirstName = req.FirstName.Trim(), LastName = req.LastName.Trim(), PhoneNumber = phone,
            IsActive = false,
            EmailConfirmed = !s.EmailVerificationEnabled,
            RegistrationStatus = channels.Count > 0 ? RegistrationStatus.PendingVerification : RegistrationStatus.PendingApproval,
        };
        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, user, req.Password);
            if (!result.Succeeded) errors["password"] = result.Errors.Select(e => e.Description).ToArray();
        }
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var accepted = TypedResults.Accepted((string?)null, new RegisterResponse(channels, channels.Count > 0
            ? "We've sent you verification codes. Enter them to continue."
            : "Thanks for registering. An administrator will review your request."));

        // Identical response for known emails, so the endpoint can't be used to discover accounts.
        if (await users.FindByEmailAsync(user.Email) is not null)
        {
            logger.LogInformation("Registration attempt for an existing email in tenant {Tenant}", tenant.Slug);
            return accepted;
        }

        var created = await users.CreateAsync(user, req.Password);
        if (!created.Succeeded)
        {
            if (created.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName")) return accepted;
            return created.ToValidationProblem();
        }
        logger.LogInformation("Self-registration {UserId} for tenant {Tenant} ({Status})", user.Id, tenant.Slug, user.RegistrationStatus);

        foreach (var channel in Remaining(user, s)) await IssueAsync(user, channel, tenant.Name, s, ct);
        if (user.RegistrationStatus == RegistrationStatus.PendingApproval) await NotifyApproversAsync(user, ct);
        return accepted;
    }

    public async Task<Results<Ok<VerifyResponse>, ValidationProblem>> VerifyAsync(VerifyRequest req, CancellationToken ct)
    {
        var invalid = Errors.Validation("code", "The code is invalid or has expired.");
        var user = await users.FindByEmailAsync(req.Email.Trim());
        if (user is null || user.RegistrationStatus != RegistrationStatus.PendingVerification) return invalid;

        var s = await settingsService.GetAsync(ct);
        if (!Remaining(user, s).Contains(req.Channel)) return await CompleteIfVerifiedAsync(user, s, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var code = await db.VerificationCodes
            .Where(c => c.UserId == user.Id && c.Channel == req.Channel && c.ConsumedAt == null)
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        if (code is null || code.ExpiresAt <= now || code.Attempts >= s.MaxAttempts) return invalid;

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code.CodeHash),
                Encoding.ASCII.GetBytes(Hash(user.Id, req.Channel, req.Code.Trim()))))
        {
            code.Attempts++;
            await db.SaveChangesAsync(ct);
            return invalid;
        }

        code.ConsumedAt = now;
        if (req.Channel == VerificationChannel.Email) user.EmailConfirmed = true;
        else user.PhoneNumberConfirmed = true;
        return await CompleteIfVerifiedAsync(user, s, ct);
    }

    public async Task ResendAsync(ResendCodeRequest req, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(req.Email.Trim());
        if (user is null || user.RegistrationStatus != RegistrationStatus.PendingVerification) return;
        var s = await settingsService.GetAsync(ct);
        if (!Remaining(user, s).Contains(req.Channel)) return;

        var now = clock.GetUtcNow().UtcDateTime;
        var open = await db.VerificationCodes.Where(c => c.UserId == user.Id && c.Channel == req.Channel && c.ConsumedAt == null).ToListAsync(ct);
        if (open.Any(c => c.CreatedAt.AddSeconds(s.ResendCooldownSeconds) > now)) return;
        foreach (var c in open) c.ConsumedAt = now;

        var tenantName = await db.Tenants.Where(t => t.Id == user.TenantId).Select(t => t.Name).SingleAsync(ct);
        await IssueAsync(user, req.Channel, tenantName, s, ct);
    }

    /// <summary>For sign-in: current state of an unapproved registration, advancing it if verification is no longer required.</summary>
    public async Task<(RegistrationStatus Status, IReadOnlyList<VerificationChannel> Remaining)> CheckPendingAsync(AppUser user, CancellationToken ct)
    {
        var s = await settingsService.GetAsync(ct);
        if (user.RegistrationStatus == RegistrationStatus.PendingVerification && Remaining(user, s).Count == 0)
            await CompleteIfVerifiedAsync(user, s, ct);
        return (user.RegistrationStatus, Remaining(user, s));
    }

    private async Task<(Tenant? Tenant, PlatformSettings Settings)> ResolveAsync(string? tenantCode, CancellationToken ct)
    {
        var s = await settingsService.GetAsync(ct);
        if (!s.SelfRegistrationEnabled || string.IsNullOrWhiteSpace(tenantCode)) return (null, s);
        var slug = tenantCode.Trim().ToLowerInvariant();
        var tenant = await db.Tenants.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Slug == slug && t.Status == TenantStatus.Active && t.SelfRegistrationEnabled, ct);
        return (tenant, s);
    }

    private async Task<Results<Ok<VerifyResponse>, ValidationProblem>> CompleteIfVerifiedAsync(AppUser user, PlatformSettings s, CancellationToken ct)
    {
        var remaining = Remaining(user, s);
        if (remaining.Count == 0) user.RegistrationStatus = RegistrationStatus.PendingApproval;
        await db.SaveChangesAsync(ct);
        if (remaining.Count == 0) await NotifyApproversAsync(user, ct);
        return TypedResults.Ok(new VerifyResponse(user.RegistrationStatus, remaining));
    }

    private async Task IssueAsync(AppUser user, VerificationChannel channel, string tenantName, PlatformSettings s, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        // One uniform digit at a time: 10^10 (the maximum length) doesn't fit in an int.
        var code = RandomNumberGenerator.GetString("0123456789", s.CodeLength);
        db.VerificationCodes.Add(new VerificationCode
        {
            UserId = user.Id, Channel = channel, CodeHash = Hash(user.Id, channel, code),
            CreatedAt = now, ExpiresAt = now.AddMinutes(s.CodeTtlMinutes),
        });
        await db.SaveChangesAsync(ct);

        try
        {
            if (channel == VerificationChannel.Email)
                await email.SendAsync(user.Email!, $"Your {tenantName} verification code",
                    $"Hi {user.FirstName},\n\nYour verification code is {code}. It expires in {s.CodeTtlMinutes} minutes.\n\n" +
                    "If you didn't register, you can ignore this message.", ct);
            else
                await sms.SendAsync(user.PhoneNumber!, $"Your {tenantName} verification code is {code}. It expires in {s.CodeTtlMinutes} minutes.", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The user can request a new code once delivery works; registration itself must not fail.
            logger.LogError(ex, "Failed to send {Channel} verification code to user {UserId}", channel, user.Id);
        }
    }

    /// <summary>Tells everyone in the tenant who can manage users that a registration is waiting.</summary>
    private async Task NotifyApproversAsync(AppUser user, CancellationToken ct)
    {
        using var _ = tenantContext.Use(user.TenantId, userName: "Registration");
        var roleIds = await db.Roles
            .Where(r => r.IsAdministrator || r.Permissions.Any(p =>
                p.Resource == AdminResources.Users && p.Action == PermissionActions.Manage && p.Scope != PermissionScope.None))
            .Select(r => r.Id).ToListAsync(ct);
        var direct = db.UserRoles.Where(ur => roleIds.Contains(ur.RoleId)).Select(ur => ur.UserId);
        var viaGroups = db.GroupMembers
            .Where(m => db.GroupRoles.Any(gr => gr.GroupId == m.GroupId && roleIds.Contains(gr.RoleId))).Select(m => m.UserId);
        var approvers = await db.Users
            .Where(u => u.TenantId == user.TenantId && u.IsActive && (direct.Contains(u.Id) || viaGroups.Contains(u.Id)))
            .Select(u => u.Id).ToListAsync(ct);

        var now = clock.GetUtcNow().UtcDateTime;
        db.Notifications.AddRange(approvers.Select(id => new Notification
        {
            UserId = id, CreatedAt = now, Link = ApprovalLink,
            Title = "New registration awaiting approval",
            Message = $"{user.FullName} ({user.Email}) registered and is waiting for approval.",
        }));
        await db.SaveChangesAsync(ct);
    }

    private static string Hash(Guid userId, VerificationChannel channel, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:N}:{channel}:{code}")));
}

public static class RegistrationEndpoints
{
    public const string RateLimitPolicy = "register";

    public static RouteGroupBuilder MapRegistration(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth").WithTags("Registration").AllowAnonymous();
        g.MapGet("/registration-options", (string? tenant, RegistrationService svc, CancellationToken ct) => svc.OptionsAsync(tenant, ct))
            .RequireRateLimiting(RateLimitPolicy);
        g.MapPost("/register", (RegisterRequest req, RegistrationService svc, CancellationToken ct) => svc.RegisterAsync(req, ct))
            .RequireRateLimiting(RateLimitPolicy);
        g.MapPost("/verify", (VerifyRequest req, RegistrationService svc, CancellationToken ct) => svc.VerifyAsync(req, ct))
            .RequireRateLimiting(RateLimitPolicy);
        g.MapPost("/resend-code", async (ResendCodeRequest req, RegistrationService svc, CancellationToken ct) =>
        {
            await svc.ResendAsync(req, ct);
            return TypedResults.Accepted((string?)null);
        }).RequireRateLimiting(RateLimitPolicy);
        return api;
    }
}
