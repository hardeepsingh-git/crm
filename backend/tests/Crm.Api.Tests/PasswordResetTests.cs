using System.Net;
using System.Text.RegularExpressions;
using Crm.Api.Auth;
using Crm.Api.Data;
using Crm.Api.Features.Security;
using Crm.Api.Features.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Api.Tests;

public partial class PasswordResetTests(CrmApiFactory factory) : IClassFixture<CrmApiFactory>
{
    private const string OldPassword = "Old#Pass12345";
    private const string NewPassword = "New#Pass12345";

    [GeneratedRegex(@"https://crm\.test/reset-password\?token=([A-Za-z0-9_-]+)")]
    private static partial Regex ResetLink();

    private async Task<(string Email, Guid Id)> CreateUserAsync(HttpClient admin)
    {
        var roles = await (await admin.GetAsync("/api/admin/roles")).ReadAsync<List<RoleItem>>();
        var email = $"reset-{Guid.NewGuid():N}@test.local";
        var response = await admin.PostJsonAsync("/api/users", new
        {
            email, firstName = "Rhea", lastName = "Reset", password = OldPassword,
            roleIds = new[] { roles.Single(r => r.Name == "Standard User").Id }, groupIds = Array.Empty<Guid>(),
        });
        response.EnsureSuccessStatusCode();
        return (email, (await response.ReadAsync<UserItem>()).Id);
    }

    private async Task<HttpResponseMessage> ForgotAsync(string email) =>
        await factory.CreateClient().PostJsonAsync("/api/auth/forgot-password", new { email });

    private async Task<HttpResponseMessage> ResetAsync(string token, string newPassword) =>
        await factory.CreateClient().PostJsonAsync("/api/auth/reset-password", new { token, newPassword });

    private async Task<HttpStatusCode> LoginStatusAsync(string email, string password) =>
        (await factory.CreateClient().PostJsonAsync("/api/auth/login", new { email, password })).StatusCode;

    private string LatestToken(string email) =>
        ResetLink().Match(factory.Messages.To(email).Last(m => m.Subject.StartsWith("Reset your")).Body).Groups[1].Value;

    [Fact]
    public async Task Reset_link_sets_a_new_password_signs_out_everywhere_clears_lockout_and_works_once()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        var (email, _) = await CreateUserAsync(admin);
        var session = await CrmApiFactory.LoginAsync(factory.CreateClient(), email, OldPassword);
        for (var i = 0; i < 5; i++) await LoginStatusAsync(email, "Wrong#Pass12345");
        Assert.Equal(HttpStatusCode.Locked, await LoginStatusAsync(email, OldPassword));

        Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(email.ToUpperInvariant())).StatusCode);
        var token = LatestToken(email);
        Assert.NotEmpty(token);

        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(token, "weak")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(token, NewPassword)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusAsync(email, OldPassword));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(email, NewPassword));
        var refresh = await factory.CreateClient().PostJsonAsync("/api/auth/refresh", new { refreshToken = session.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Contains(factory.Messages.To(email), m => m.Subject == "Your password was changed");

        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(token, "Another#Pass123")).StatusCode);
    }

    [Fact]
    public async Task Requests_reveal_nothing_skip_inactive_users_respect_the_cooldown_and_links_expire()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();

        var unknown = $"nobody-{Guid.NewGuid():N}@test.local";
        Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(unknown)).StatusCode);
        Assert.Empty(factory.Messages.To(unknown));

        var (inactive, inactiveId) = await CreateUserAsync(admin);
        (await admin.PutJsonAsync($"/api/users/{inactiveId}", new
        {
            firstName = "Rhea", lastName = "Reset", isActive = false, roleIds = Array.Empty<Guid>(), groupIds = Array.Empty<Guid>(),
        })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(inactive)).StatusCode);
        Assert.Empty(factory.Messages.To(inactive));

        var (email, _) = await CreateUserAsync(admin);
        await ForgotAsync(email);
        await ForgotAsync(email);
        Assert.Single(factory.Messages.To(email));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
            await db.PasswordResetTokens.ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(LatestToken(email), NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync("not-a-real-token", NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(email, OldPassword));
    }
}
