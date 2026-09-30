using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Crm.Api.Auth;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Features.Accounts;
using Crm.Api.Features.Activities;
using Crm.Api.Features.Contacts;
using Crm.Api.Features.Dashboard;
using Crm.Api.Features.Leads;
using Crm.Api.Features.Opportunities;
using Crm.Api.Features.Users;
using Crm.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.AddSingleton(TimeProvider.System);
services.AddDbContext<CrmDbContext>((sp, options) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var csb = new NpgsqlConnectionStringBuilder(config.GetConnectionString("Crm")
        ?? throw new InvalidOperationException("Connection string 'Crm' is not configured."));
    if (config["Database:Password"] is { Length: > 0 } password) csb.Password = password;
    options.UseNpgsql(csb.ConnectionString);
});

services.AddIdentityCore<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 10;
        o.Password.RequireDigit = true;
        o.Password.RequireLowercase = true;
        o.Password.RequireUppercase = true;
        o.Password.RequireNonAlphanumeric = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<CrmDbContext>()
    .AddDefaultTokenProviders();

services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection("Jwt"))
    .Validate(o => System.Text.Encoding.UTF8.GetByteCount(o.SigningKey) >= 32, "Jwt:SigningKey must be at least 32 bytes.")
    .ValidateOnStart();
services.AddScoped<TokenService>();

services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((o, jwt) =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Value.Issuer,
            ValidAudience = jwt.Value.Audience,
            IssuerSigningKey = jwt.Value.GetKey(),
            ValidateIssuerSigningKey = true,
            NameClaimType = "name",
            RoleClaimType = "role",
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

services.AddAuthorizationBuilder()
    .AddPolicy(Policies.AdminOnly, p => p.RequireRole(Roles.Admin))
    .AddPolicy(Policies.ManagerOrAdmin, p => p.RequireRole(Roles.Admin, Roles.Manager));

services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = ctx.RequestServices.GetRequiredService<IConfiguration>().GetValue("RateLimiting:AuthPermitPerMinute", 10),
            Window = TimeSpan.FromMinutes(1),
        }));
});

services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()));

services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Only enable (ForwardedHeaders:Enabled) when the API is reachable solely through the trusted proxy.
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
services.AddValidation();
services.AddProblemDetails();
services.AddOpenApi(o => o.AddDocumentTransformer<BearerSecuritySchemeTransformer>());
services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

var app = builder.Build();

if (app.Configuration.GetValue<bool>("ForwardedHeaders:Enabled")) app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseSecurityHeaders();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Configuration.GetValue("OpenApi:Enabled", app.Environment.IsDevelopment()))
{
    app.MapOpenApi();
    app.MapScalarApiReference(o => o.WithTitle("CRM API"));
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

var api = app.MapGroup("/api");
api.MapAuth();
var secured = api.MapGroup("").RequireAuthorization();
secured.MapDashboard();
secured.MapAccounts();
secured.MapContacts();
secured.MapLeads();
secured.MapOpportunities();
secured.MapActivities();
secured.MapUsers();

await DbInitializer.InitializeAsync(app.Services, app.Configuration, app.Logger);
await app.RunAsync();

public partial class Program;
