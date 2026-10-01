using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Metadata;
using Crm.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Interviews;

/// <param name="Reached">Applications that got at least this far (including ones later rejected or hired).</param>
/// <param name="ConversionToNext">Share of those that reached the next open stage (or were hired, for the last one); null for the last stage.</param>
/// <param name="AverageDaysInStage">Average time spent in the stage by applications that have left it.</param>
public record FunnelStage(string Key, string Label, string Color, int Current, int Reached, double? ConversionToNext, double? AverageDaysInStage);

public record SourceRow(string Source, int Applications, int Hired, double HireRate);

public record JobRow(Guid Id, string Title, int Applications, int Open, int Hired, IReadOnlyDictionary<string, int> ByStage);

/// <param name="AcceptanceRate">Accepted share of the offers candidates answered or let expire.</param>
public record OfferStats(int Sent, int Accepted, int Declined, int Expired, int Pending, double? AcceptanceRate, double? AverageDaysToRespond);

public record HiringAnalytics(
    int Days, int Applications, int Open, int Hired, int Rejected, double? AverageDaysToHire, double? MedianDaysToHire,
    IReadOnlyList<FunnelStage> Funnel, IReadOnlyList<SourceRow> Sources, IReadOnlyList<JobRow> Jobs,
    int InterviewsNext7Days, int FeedbackOutstanding, OfferStats Offers);

/// <summary>Recruitment funnel, time to hire, sources and jobs for the applications the caller can read.</summary>
public static class HiringAnalyticsEndpoints
{
    public const int MaxApplications = 20000;

    public static RouteGroupBuilder MapHiringAnalytics(this RouteGroupBuilder api)
    {
        api.MapGet("/recruiting/analytics", Get).WithTags("Interviews")
            .RequirePermission(InterviewEndpoints.ApplicationKey, PermissionActions.Read);
        return api;
    }

    private static async Task<Results<Ok<HiringAnalytics>, NotFound>> Get(
        Access access, CrmDbContext db, MetadataService meta, TimeProvider clock, CancellationToken ct, int days = 90, Guid? positionId = null)
    {
        if (await meta.ObjectAsync(InterviewEndpoints.ApplicationKey, ct) is not { IsActive: true, HasPipeline: true } definition) return TypedResults.NotFound();
        var p = await access.GetAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        days = Math.Clamp(days, 7, 730);
        var from = now.AddDays(-days);

        var query = p.Filter(db.CustomRecords.AsNoTracking().Where(r => r.ObjectDefinitionId == definition.Id && r.CreatedAt >= from),
            InterviewEndpoints.ApplicationKey);
        if (positionId is { } job) query = query.WhereCustomField(db, "position", System.Text.Json.Nodes.JsonValue.Create(job.ToString()));
        var apps = await query.OrderByDescending(r => r.CreatedAt).Take(MaxApplications).ToListAsync(ct);

        var stages = await meta.StagesAsync(InterviewEndpoints.ApplicationKey, ct);
        var open = stages.Where(s => s.Category == StageCategory.Open).ToList();
        var order = open.Select((s, i) => (s.Key, i)).ToDictionary(x => x.Key, x => x.i);
        var category = stages.ToDictionary(s => s.Key, s => s.Category);

        var history = await HistoryAsync(db, apps.Select(a => a.Id).ToList(), ct);
        var stayed = open.ToDictionary(s => s.Key, _ => new List<double>());
        var reached = open.ToDictionary(s => s.Key, _ => 0);
        foreach (var app in apps)
        {
            var path = Path(app, history.GetValueOrDefault(app.Id) ?? []);
            for (var i = 0; i < path.Count - 1; i++)
                if (stayed.TryGetValue(path[i].Stage, out var list)) list.Add((path[i + 1].At - path[i].At).TotalDays);

            // Furthest open stage the application reached; a hire has passed every open stage.
            var furthest = category.GetValueOrDefault(app.Stage) == StageCategory.Won
                ? open.Count - 1
                : path.Select(x => order.GetValueOrDefault(x.Stage, -1)).DefaultIfEmpty(-1).Max();
            for (var i = 0; i <= furthest; i++) reached[open[i].Key]++;
        }

        var hiredApps = apps.Where(a => category.GetValueOrDefault(a.Stage) == StageCategory.Won && a.ClosedAt is not null).ToList();
        var toHire = hiredApps.Select(a => (a.ClosedAt!.Value - a.CreatedAt).TotalDays).OrderBy(d => d).ToList();
        var hiredCount = apps.Count(a => category.GetValueOrDefault(a.Stage) == StageCategory.Won);

        var funnel = stages.Select(s =>
        {
            var current = apps.Count(a => a.Stage == s.Key);
            if (s.Category != StageCategory.Open) return new FunnelStage(s.Key, s.Label, s.Color, current, current, null, null);
            var i = order[s.Key];
            double? conversion = i + 1 < open.Count
                ? Rate(reached[open[i + 1].Key], reached[s.Key])
                : Rate(hiredCount, reached[s.Key]);
            var spent = stayed[s.Key];
            return new FunnelStage(s.Key, s.Label, s.Color, current, reached[s.Key], conversion, spent.Count == 0 ? null : Math.Round(spent.Average(), 1));
        }).ToList();

        var sources = apps.GroupBy(a => Cf.AsText(Cf.Parse(a.CustomFields)["source"]) ?? "Not set")
            .Select(g =>
            {
                var hired = g.Count(a => category.GetValueOrDefault(a.Stage) == StageCategory.Won);
                return new SourceRow(g.Key, g.Count(), hired, Rate(hired, g.Count()) ?? 0);
            })
            .OrderByDescending(r => r.Applications).ToList();

        var jobIds = apps.Select(a => Guid.TryParse(Cf.AsText(Cf.Parse(a.CustomFields)["position"]), out var j) ? j : Guid.Empty)
            .Where(j => j != Guid.Empty).Distinct().ToList();
        var titles = await db.CustomRecords.AsNoTracking().Where(r => jobIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var jobs = apps.GroupBy(a => Guid.TryParse(Cf.AsText(Cf.Parse(a.CustomFields)["position"]), out var j) ? j : Guid.Empty)
            .Where(g => g.Key != Guid.Empty)
            .Select(g => new JobRow(g.Key, titles.GetValueOrDefault(g.Key, "Unknown job"), g.Count(), g.Count(a => a.ClosedAt == null),
                g.Count(a => category.GetValueOrDefault(a.Stage) == StageCategory.Won),
                g.GroupBy(a => a.Stage).ToDictionary(s => s.Key, s => s.Count())))
            .OrderByDescending(r => r.Open).ThenBy(r => r.Title).ToList();

        var ids = apps.Select(a => a.Id).ToList();
        var interviews = await db.Interviews.AsNoTracking().Where(i => ids.Contains(i.ApplicationId) && i.Status != InterviewStatus.Cancelled)
            .Select(i => new
            {
                i.ScheduledAt, i.Status,
                Pending = i.Participants.Count(x => !i.Scorecards.Any(s => s.InterviewerId == x.UserId)),
            }).ToListAsync(ct);
        var offers = new List<(OfferStatus Status, DateTime? SentAt, DateTime? RespondedAt)>();
        foreach (var chunk in ids.Chunk(1000))
            offers.AddRange((await db.Offers.AsNoTracking().Where(o => chunk.Contains(o.ApplicationId) && o.SentAt != null)
                .Select(o => new { o.Status, o.ExpiresAt, o.SentAt, o.RespondedAt }).ToListAsync(ct))
                .Select(o => (o.Status == OfferStatus.Sent && o.ExpiresAt <= now ? OfferStatus.Expired : o.Status, o.SentAt, o.RespondedAt)));
        var accepted = offers.Count(o => o.Status == OfferStatus.Accepted);
        var declined = offers.Count(o => o.Status == OfferStatus.Declined);
        var expired = offers.Count(o => o.Status == OfferStatus.Expired);
        var responseDays = offers.Where(o => o.RespondedAt is not null).Select(o => (o.RespondedAt!.Value - o.SentAt!.Value).TotalDays).ToList();
        var offerStats = new OfferStats(offers.Count, accepted, declined, expired, offers.Count(o => o.Status == OfferStatus.Sent),
            Rate(accepted, accepted + declined + expired), responseDays.Count == 0 ? null : Math.Round(responseDays.Average(), 1));

        return TypedResults.Ok(new HiringAnalytics(
            days, apps.Count, apps.Count(a => a.ClosedAt == null), hiredCount,
            apps.Count(a => category.GetValueOrDefault(a.Stage) == StageCategory.Lost),
            toHire.Count == 0 ? null : Math.Round(toHire.Average(), 1),
            toHire.Count == 0 ? null : Math.Round(toHire.Count % 2 == 1 ? toHire[toHire.Count / 2] : (toHire[toHire.Count / 2 - 1] + toHire[toHire.Count / 2]) / 2, 1),
            funnel, sources, jobs,
            interviews.Count(i => i.Status == InterviewStatus.Scheduled && i.ScheduledAt >= now && i.ScheduledAt < now.AddDays(7)),
            interviews.Where(i => i.ScheduledAt < now).Sum(i => i.Pending), offerStats));
    }

    private static double? Rate(int part, int whole) => whole == 0 ? null : Math.Round((double)part / whole, 3);

    /// <summary>Stage changes per application, oldest first, from the audit log (<c>stage: [old, new]</c>).</summary>
    private static async Task<Dictionary<Guid, List<(DateTime At, string? From, string To)>>> HistoryAsync(
        CrmDbContext db, List<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, List<(DateTime, string?, string)>>();
        foreach (var chunk in ids.Chunk(1000))
        {
            var rows = await db.AuditLogs.AsNoTracking()
                .Where(a => a.EntityType == InterviewEndpoints.ApplicationKey && chunk.Contains(a.EntityId) && a.Action == AuditAction.Updated)
                .OrderBy(a => a.Timestamp)
                .Select(a => new { a.EntityId, a.Timestamp, a.Changes })
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                if (Cf.Parse(row.Changes)["stage"] is not System.Text.Json.Nodes.JsonArray { Count: 2 } change || Cf.AsText(change[1]) is not { } to) continue;
                if (!result.TryGetValue(row.EntityId, out var list)) result[row.EntityId] = list = [];
                list.Add((row.Timestamp, Cf.AsText(change[0]), to));
            }
        }
        return result;
    }

    /// <summary>The stages an application went through with the time it entered each, starting with the one it was created in.</summary>
    private static List<(string Stage, DateTime At)> Path(CustomRecord app, List<(DateTime At, string? From, string To)> changes)
    {
        var path = new List<(string, DateTime)> { (changes.Count > 0 ? changes[0].From ?? app.Stage : app.Stage, app.CreatedAt) };
        path.AddRange(changes.Select(c => (c.To, c.At)));
        return path;
    }
}
