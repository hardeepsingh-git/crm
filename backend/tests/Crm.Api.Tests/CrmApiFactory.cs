using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Crm.Api.Auth;
using Crm.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Crm.Api.Tests;

public sealed class CrmApiFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "Admin#Pass12345";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Crm", "Host=unused");
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-that-is-long-enough-for-hs256!!");
        builder.UseSetting("Database:Strategy", "EnsureCreated");
        builder.UseSetting("Seed:AdminEmail", AdminEmail);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
        builder.UseSetting("Seed:DemoData", "false");
        builder.UseSetting("RateLimiting:AuthPermitPerMinute", "10000");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<CrmDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<CrmDbContext>>();
            services.AddDbContext<CrmDbContext>(o => o.UseSqlite(_connection));
        });
    }

    public async Task<HttpClient> CreateAuthenticatedClientAsync(string email = AdminEmail, string password = AdminPassword)
    {
        var client = CreateClient();
        var auth = await LoginAsync(client, email, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    public static async Task<AuthResponse> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password }, Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
    }

    /// <summary>Creates a user via the admin API and returns an authenticated client for it.</summary>
    public async Task<HttpClient> CreateUserClientAsync(string role)
    {
        var admin = await CreateAuthenticatedClientAsync();
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@test.local";
        const string password = "User#Pass12345";
        var response = await admin.PostAsJsonAsync("/api/users",
            new { email, firstName = role, lastName = "User", password, roles = new[] { role } }, Json);
        response.EnsureSuccessStatusCode();
        return await CreateAuthenticatedClientAsync(email, password);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}

public static class HttpExtensions
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(CrmApiFactory.Json))!;

    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PostAsJsonAsync(url, body, CrmApiFactory.Json);

    public static Task<HttpResponseMessage> PutJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PutAsJsonAsync(url, body, CrmApiFactory.Json);

    public static Task<HttpResponseMessage> PatchJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PatchAsJsonAsync(url, body, CrmApiFactory.Json);
}
