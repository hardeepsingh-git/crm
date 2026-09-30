using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Crm.Api.Data;
using Crm.Api.Domain;
using Microsoft.AspNetCore.Identity;
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

public record UserInfo(Guid Id, string Email, string FirstName, string LastName, string FullName, IReadOnlyList<string> Roles);

public record AuthResponse(string AccessToken, DateTime ExpiresAt, string RefreshToken, UserInfo User);

public sealed class TokenService(IOptions<JwtOptions> options, CrmDbContext db, UserManager<AppUser> users, TimeProvider clock)
{
    private readonly JwtOptions _jwt = options.Value;

    public async Task<AuthResponse> IssueAsync(AppUser user, CancellationToken ct)
    {
        var roles = (await users.GetRolesAsync(user)).ToList();
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_jwt.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email!),
            new(JwtRegisteredClaimNames.Name, user.FullName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        claims.AddRange(roles.Select(r => new Claim("role", r)));

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

        return new AuthResponse(accessToken, expires, refreshToken, ToUserInfo(user, roles));
    }

    /// <summary>Rotates a refresh token. Reuse of a revoked token revokes every token of that user.</summary>
    public async Task<AuthResponse?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var stored = await db.RefreshTokens.Include(t => t.User).SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored?.User is null) return null;

        var now = clock.GetUtcNow().UtcDateTime;
        if (stored.RevokedAt is not null)
        {
            await RevokeAllAsync(stored.UserId, ct);
            return null;
        }
        if (stored.ExpiresAt <= now || !stored.User.IsActive) return null;

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

    public static UserInfo ToUserInfo(AppUser user, IReadOnlyList<string> roles) =>
        new(user.Id, user.Email!, user.FirstName, user.LastName, user.FullName, roles);

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
