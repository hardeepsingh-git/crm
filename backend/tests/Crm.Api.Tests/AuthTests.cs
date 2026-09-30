using System.Net;
using System.Net.Http.Headers;
using Crm.Api.Auth;
using Microsoft.AspNetCore.Hosting;

namespace Crm.Api.Tests;

public class AuthTests(CrmApiFactory factory) : IClassFixture<CrmApiFactory>
{
    [Fact]
    public async Task Liveness_endpoint_is_anonymous()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_requires_token()
    {
        var response = await factory.CreateClient().GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_401()
    {
        var response = await factory.CreateClient().PostJsonAsync("/api/auth/login",
            new { email = CrmApiFactory.AdminEmail, password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_returns_tokens_and_me_returns_admin()
    {
        var client = factory.CreateClient();
        var auth = await CrmApiFactory.LoginAsync(client, CrmApiFactory.AdminEmail, CrmApiFactory.AdminPassword);
        Assert.False(string.IsNullOrEmpty(auth.AccessToken));
        Assert.False(string.IsNullOrEmpty(auth.RefreshToken));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var me = await (await client.GetAsync("/api/auth/me")).ReadAsync<UserInfo>();
        Assert.Equal(CrmApiFactory.AdminEmail, me.Email);
        Assert.Contains("Administrator", me.Roles);
        Assert.True(me.IsAdministrator);
        Assert.True(me.IsPlatformAdmin);
        Assert.Equal("default", me.Tenant.Slug);
    }

    [Fact]
    public async Task Refresh_rotates_token_and_reuse_revokes_family()
    {
        var client = factory.CreateClient();
        var first = await CrmApiFactory.LoginAsync(client, CrmApiFactory.AdminEmail, CrmApiFactory.AdminPassword);

        var refreshed = await client.PostJsonAsync("/api/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var second = await refreshed.ReadAsync<AuthResponse>();
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        var reuse = await client.PostJsonAsync("/api/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);

        var afterReuse = await client.PostJsonAsync("/api/auth/refresh", new { refreshToken = second.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
    }

    [Fact]
    public async Task Refresh_has_its_own_rate_limit_and_login_stays_limited()
    {
        using var limited = factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:AuthPermitPerMinute", "2"));
        var client = limited.CreateClient();
        var auth = await CrmApiFactory.LoginAsync(client, CrmApiFactory.AdminEmail, CrmApiFactory.AdminPassword);

        for (var i = 0; i < 5; i++)
        {
            var refreshed = await client.PostJsonAsync("/api/auth/refresh", new { refreshToken = auth.RefreshToken });
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
            auth = await refreshed.ReadAsync<AuthResponse>();
        }

        var second = await client.PostJsonAsync("/api/auth/login", new { email = CrmApiFactory.AdminEmail, password = CrmApiFactory.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var third = await client.PostJsonAsync("/api/auth/login", new { email = CrmApiFactory.AdminEmail, password = CrmApiFactory.AdminPassword });
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_refresh_token()
    {
        var client = factory.CreateClient();
        var auth = await CrmApiFactory.LoginAsync(client, CrmApiFactory.AdminEmail, CrmApiFactory.AdminPassword);

        var logout = await client.PostJsonAsync("/api/auth/logout", new { refreshToken = auth.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var refresh = await client.PostJsonAsync("/api/auth/refresh", new { refreshToken = auth.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }
}
