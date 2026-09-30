using Crm.Api.Data;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;

namespace Crm.Api.Infrastructure;

public sealed class DatabaseHealthCheck(CrmDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Cannot connect to the database.");
}

public sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Info.Title = "CRM API";
        document.Info.Description = "REST API for the CRM system. Authenticate via POST /api/auth/login and send the access token as a Bearer token.";
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
        };
        document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
        return Task.CompletedTask;
    }
}

public static class SecurityHeaders
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var h = ctx.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        await next();
    });
}
