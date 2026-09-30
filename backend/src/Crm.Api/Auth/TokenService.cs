using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Crm.Api.Auth;

public class JwtOptions
{
    public string Issuer { get; set; } = "crm-api";
    public string Audience { get; set; } = "crm-web";
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 7;

    public SymmetricSecurityKey GetKey() => new(Encoding.UTF8.GetBytes(SigningKey));
}

public record TenantInfo(Guid Id, string Name, string Slug, string Currency, string Template);

public record UserInfo(
    Guid Id, string Email, string FirstName, string LastName, string FullName,
    IReadOnlyList<string> Roles, TenantInfo Tenant, bool IsAdministrator, bool IsPlatformAdmin,
    Dictionary<string, Dictionary<string, PermissionScope>> Permissions, IReadOnlyCollection<Guid> TeamUserIds);

public record AuthResponse(string AccessToken, DateTime ExpiresAt, string RefreshToken, UserInfo User);

public sealed class TokenService(IOptions<JwtOptions> options, CrmDbContext db, PermissionService permissions, TimeProvider clock)
{
    private readonly JwtOptions _jwt = options.Value;

    public async Task<UserInfo?> DescribeAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Tenant).SingleOrDefaultAsync(u => u.Id == userId, ct);
        var effective = await permissions.GetAsync(userId, ct);
        if (user?.Tenant is null || effective is null) return null;
        var t = user.Tenant;
        return new UserInfo(user.Id, user.Email!, user.FirstName, user.LastName, user.FullName, effective.RoleNames,
            new TenantInfo(t.Id, t.Name, t.Slug, t.Currency, t.Template), effective.IsAdministrator, effective.IsPlatformAdmin,
            effective.ToDictionary(), effective.TeamUserIds.ToList());
    }

    public async Task<AuthResponse> IssueAsync(AppUser user, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_jwt.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(Claims.Subject, user.Id.ToString()),
            new(Claims.Email, user.Email!),
            new(Claims.Name, user.FullName),
            new(Claims.Tenant, user.TenantId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var accessToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(_jwt.GetKey(), SecurityAlgorithms.HmacSha256),
        });

        var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash(refreshToken),
            CreatedAt = now,
            ExpiresAt = now.AddDays(_jwt.RefreshTokenDays),
        });
        await db.SaveChangesAsync(ct);

        var info = await DescribeAsync(user.Id, ct) ?? throw new InvalidOperationException("User vanished while signing in.");
        return new AuthResponse(accessToken, expires, refreshToken, info);
    }

    /// <summary>Rotates a refresh token. Reuse of a revoked token revokes every token of that user.</summary>
    public async Task<AuthResponse?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var stored = await db.RefreshTokens.Include(t => t.User).ThenInclude(u => u!.Tenant)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored?.User is null) return null;

        var now = clock.GetUtcNow().UtcDateTime;
        if (stored.RevokedAt is not null)
        {
            await RevokeAllAsync(stored.UserId, ct);
            return null;
        }
        if (stored.ExpiresAt <= now || !stored.User.IsActive || stored.User.Tenant?.Status != TenantStatus.Active) return null;

        stored.RevokedAt = now;
        return await IssueAsync(stored.User, ct);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var stored = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null, ct);
        if (stored is null) return;
        stored.RevokedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    public async Task RevokeAllAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in active) t.RevokedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
