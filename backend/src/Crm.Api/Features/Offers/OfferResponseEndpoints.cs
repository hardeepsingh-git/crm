using System.ComponentModel.DataAnnotations;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Offers;

/// <summary>What the candidate sees. Pay details and the letter only while the offer is open or after they've accepted it.</summary>
public record PublicOffer(
    string Organisation, string CandidateName, string JobTitle, OfferStatus Status, DateTime ExpiresAt,
    decimal? Salary, string? Currency, PayPeriod? PayPeriod, DateOnly? StartDate, string? Letter, DateTime? RespondedAt, string? SignedName);

public record AcceptOfferRequest([MaxLength(200)] string? SignedName, bool Agree);

public record DeclineOfferRequest([MaxLength(2000)] string? Reason);

public record OfferAnswered(OfferStatus Status, string Message);

/// <summary>
/// Anonymous endpoints behind the candidate's offer link. The link's token is the only credential: it enters the offer's
/// tenant, allows nothing but reading and answering that one offer, and stops working when the offer is withdrawn or re-sent.
/// </summary>
public static class OfferResponseEndpoints
{
    public const string RateLimitPolicy = "offer-response";
    public const string Actor = "Candidate";

    public static RouteGroupBuilder MapOfferResponses(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/offer-response/{token}").WithTags("Offers").AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        g.MapGet("/", Get);
        // Bearer-only API (no cookies), so cross-site request forgery doesn't apply.
        g.MapPost("/accept", Accept).DisableAntiforgery();
        g.MapPost("/decline", Decline).DisableAntiforgery();
        return api;
    }

    private sealed record Entry(Offer Offer, CustomRecord Application, Tenant Organisation);

    private static async Task<Entry?> EnterAsync(string token, CrmDbContext db, TenantContext tenant, CancellationToken ct)
    {
        if (token.Length is < 32 or > 64) return null;
        var hash = OfferEndpoints.HashToken(token);
        var offer = await db.Offers.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.TokenHash == hash, ct);
        if (offer is null) return null;
        var org = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == offer.TenantId && t.Status == TenantStatus.Active, ct);
        if (org is null) return null;
        tenant.Set(org.Id, userName: Actor);
        var app = await db.CustomRecords.FirstOrDefaultAsync(r => r.Id == offer.ApplicationId, ct);
        return app is null ? null : new Entry(offer, app, org);
    }

    private static PublicOffer ToPublic(Entry e)
    {
        var o = e.Offer;
        var details = o.Status is OfferStatus.Sent or OfferStatus.Accepted;
        return new PublicOffer(e.Organisation.Name, e.Application.Name, o.JobTitle, o.Status, o.ExpiresAt,
            details ? o.Salary : null, details ? o.Currency : null, details ? o.PayPeriod : null, details ? o.StartDate : null,
            details ? o.Letter : null, o.RespondedAt, o.Status == OfferStatus.Accepted ? o.SignedName : null);
    }

    private static async Task<IResult> Get(string token, CrmDbContext db, TenantContext tenant, TimeProvider clock, CancellationToken ct)
    {
        if (await EnterAsync(token, db, tenant, ct) is not { } e) return TypedResults.NotFound();
        if (OfferEndpoints.ExpireIfDue(e.Offer, clock.GetUtcNow().UtcDateTime)) await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToPublic(e));
    }

    /// <summary>Case and spacing don't matter when the candidate types their name to sign.</summary>
    public static string NormalizeName(string? name) =>
        string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static async Task<IResult> Accept(
        string token, AcceptOfferRequest req, HttpContext http, CrmDbContext db, TenantContext tenant, TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        if (await EnterAsync(token, db, tenant, ct) is not { } e) return TypedResults.NotFound();
        var now = clock.GetUtcNow().UtcDateTime;
        if (await NotOpenAsync(e, db, now, ct) is { } closed) return closed;
        var errors = new Dictionary<string, string[]>();
        if (NormalizeName(req.SignedName).Length == 0 || NormalizeName(req.SignedName) != NormalizeName(e.Application.Name))
            errors["signedName"] = [$"Type your full name exactly as shown on the offer: {e.Application.Name}."];
        if (!req.Agree) errors["agree"] = ["Tick the box to confirm you accept the offer."];
        if (errors.Count > 0) return Errors.Validation(errors);

        Record(e.Offer, http, now, OfferStatus.Accepted);
        e.Offer.SignedName = req.SignedName!.Trim();
        Inform(db, e, now, "Offer accepted",
            $"{e.Application.Name} accepted the offer for {e.Offer.JobTitle}. Move the application to Hired when you're ready.",
            $"Accepted by typing \"{e.Offer.SignedName}\" on {now:d MMM yyyy HH:mm} UTC from {e.Offer.ResponseIp ?? "an unknown address"}.");
        await db.SaveChangesAsync(ct);
        loggers.CreateLogger("Crm.Api.Offers").LogInformation("Offer {OfferId} accepted in tenant {Slug}", e.Offer.Id, e.Organisation.Slug);
        return TypedResults.Ok(new OfferAnswered(OfferStatus.Accepted, "Thank you. Your acceptance has been recorded and the team will be in touch about next steps."));
    }

    private static async Task<IResult> Decline(
        string token, DeclineOfferRequest req, HttpContext http, CrmDbContext db, TenantContext tenant, TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        if (await EnterAsync(token, db, tenant, ct) is not { } e) return TypedResults.NotFound();
        var now = clock.GetUtcNow().UtcDateTime;
        if (await NotOpenAsync(e, db, now, ct) is { } closed) return closed;

        Record(e.Offer, http, now, OfferStatus.Declined);
        e.Offer.DeclineReason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
        Inform(db, e, now, "Offer declined", $"{e.Application.Name} declined the offer for {e.Offer.JobTitle}.",
            e.Offer.DeclineReason is null ? "No reason given." : $"Reason: {e.Offer.DeclineReason}");
        await db.SaveChangesAsync(ct);
        loggers.CreateLogger("Crm.Api.Offers").LogInformation("Offer {OfferId} declined in tenant {Slug}", e.Offer.Id, e.Organisation.Slug);
        return TypedResults.Ok(new OfferAnswered(OfferStatus.Declined, "Thank you for letting us know. We've passed your decision on to the team."));
    }

    private static async Task<IResult?> NotOpenAsync(Entry e, CrmDbContext db, DateTime now, CancellationToken ct)
    {
        if (OfferEndpoints.ExpireIfDue(e.Offer, now)) await db.SaveChangesAsync(ct);
        return e.Offer.Status switch
        {
            OfferStatus.Sent => null,
            OfferStatus.Expired => Errors.Validation("status", "This offer has expired. Please contact the team if you're still interested."),
            _ => Errors.Validation("status", $"You've already {e.Offer.Status.ToString().ToLowerInvariant()} this offer."),
        };
    }

    private static void Record(Offer offer, HttpContext http, DateTime now, OfferStatus status)
    {
        offer.Status = status;
        offer.RespondedAt = now;
        offer.ResponseIp = http.Connection.RemoteIpAddress?.ToString();
        var agent = http.Request.Headers.UserAgent.ToString();
        offer.ResponseUserAgent = agent.Length == 0 ? null : agent[..Math.Min(500, agent.Length)];
    }

    /// <summary>A note on the application's timeline and a notification to whoever sent the offer and the application's owner.</summary>
    private static void Inform(CrmDbContext db, Entry e, DateTime now, string title, string message, string details)
    {
        db.Activities.Add(new Activity
        {
            Type = ActivityType.Note, OwnerId = e.Application.OwnerId, CustomRecordId = e.Application.Id, CompletedAt = now,
            Subject = title, Description = details.Length <= 4000 ? details : details[..4000],
        });
        foreach (var userId in new[] { e.Offer.SentById, e.Application.OwnerId }.OfType<Guid>().Distinct())
            db.Notifications.Add(new Notification
            {
                UserId = userId, Title = title, Message = message.Length <= 1000 ? message : message[..1000],
                Link = $"/o/{OfferKeys.ApplicationKey}/{e.Application.Id}", CreatedAt = now,
            });
    }
}
