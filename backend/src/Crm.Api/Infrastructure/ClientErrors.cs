using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Crm.Api.Infrastructure;

public record ClientErrorReport([Required, MaxLength(1000)] string Message, [MaxLength(4000)] string? Stack, [MaxLength(500)] string? Url);

/// <summary>Browser errors reported by signed-in users, logged so they reach the same error tracker as server errors.</summary>
public static class ClientErrorEndpoints
{
    public const string RateLimitPolicy = "client-errors";

    public static RouteGroupBuilder MapClientErrors(this RouteGroupBuilder api)
    {
        api.MapPost("/client-errors", (ClientErrorReport report, ILoggerFactory loggers) =>
        {
            loggers.CreateLogger("Crm.Web.ClientErrors")
                .LogError("Browser error at {Url}: {Message}\n{Stack}", report.Url, report.Message, report.Stack);
            return TypedResults.NoContent();
        }).WithTags("Diagnostics").RequireRateLimiting(RateLimitPolicy);
        return api;
    }
}
