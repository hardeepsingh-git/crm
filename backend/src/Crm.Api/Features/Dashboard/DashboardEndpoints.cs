using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Features.Activities;
using Crm.Api.Metadata;
using Crm.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using static Crm.Api.Domain.PermissionActions;

namespace Crm.Api.Features.Dashboard;

public record StageSummary(string Stage, string Label, string Color, int Count, decimal Amount);

public record SourceSummary(string Source, int Count);

public record MonthlyWon(int Year, int Month, int Count, decimal Amount);

public record CustomPipelineSummary(string ObjectKey, string Label, string Icon, int OpenCount, IReadOnlyList<StageSummary> Stages);

/// <summary>Sections the caller cannot read are returned empty/zero; <see cref="Sections"/> lists what is populated.</summary>
public record DashboardSummary(
    IReadOnlyList<string> Sections,
    int AccountCount, int ContactCount, int OpenLeadCount, int OpenOpportunityCount,
    decimal OpenPipelineAmount, decimal WeightedPipelineAmount,
    int WonThisMonthCount, decimal WonThisMonthAmount, double WinRate, int OverdueActivityCount,
    IReadOnlyList<StageSummary> PipelineByStage, IReadOnlyList<SourceSummary> LeadsBySource,
    IReadOnlyList<MonthlyWon> WonByMonth, IReadOnlyList<ActivityItem> UpcomingActivities,
    IReadOnlyList<CustomPipelineSummary> CustomPipelines);

public static class DashboardEndpoints
{
    public static RouteGroupBuilder MapDashboard(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", Get).WithTags("Dashboard");
        return api;
    }

    private static async Task<Ok<DashboardSummary>> Get(
        Access access, CrmDbContext db, MetadataService meta, TimeProvider clock, CancellationToken ct, bool mine = false)
    {
        var p = await access.GetAsync(ct);
        var me = p.UserId;
        var now = clock.GetUtcNow().UtcDateTime;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var sixMonthsStart = monthStart.AddMonths(-5);
        var objects = await meta.ObjectsAsync(ct);
        bool Visible(string key) => objects.Any(o => o.Key == key && o.IsActive) && p.Can(key, Read);
        IQueryable<T> Scoped<T>(IQueryable<T> q, string key) where T : OwnedEntity
        {
            var scoped = p.Filter(q.AsNoTracking(), key);
            return mine ? scoped.Where(x => x.OwnerId == me) : scoped;
        }

        var sections = new List<string>();
        int accountCount = 0, contactCount = 0, openLeadCount = 0, overdue = 0, wonThisMonthCount = 0;
        decimal wonThisMonthAmount = 0;
        double winRate = 0;
        var pipelineByStage = new List<StageSummary>();
        var leadsBySource = new List<SourceSummary>();
        var wonByMonth = new List<MonthlyWon>();
        var upcoming = new List<ActivityItem>();
        var openGroups = new List<(string Stage, int Probability, int Count, decimal Amount)>();

        if (Visible(ObjectKeys.Account)) { accountCount = await Scoped(db.Accounts, ObjectKeys.Account).CountAsync(ct); sections.Add(ObjectKeys.Account); }
        if (Visible(ObjectKeys.Contact)) { contactCount = await Scoped(db.Contacts, ObjectKeys.Contact).CountAsync(ct); sections.Add(ObjectKeys.Contact); }

        if (Visible(ObjectKeys.Lead))
        {
            var leads = Scoped(db.Leads, ObjectKeys.Lead);
            openLeadCount = await leads.CountAsync(l => l.Status != LeadStatus.Converted && l.Status != LeadStatus.Unqualified, ct);
            leadsBySource = (await leads.GroupBy(l => l.Source).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
                .Select(g => new SourceSummary(g.Key, g.Count)).OrderByDescending(s => s.Count).ToList();
            sections.Add(ObjectKeys.Lead);
        }

        if (Visible(ObjectKeys.Opportunity))
        {
            var stages = await meta.StagesAsync(ObjectKeys.Opportunity, ct);
            var wonKeys = stages.Where(s => s.Category == StageCategory.Won).Select(s => s.Key).ToList();
            var opps = Scoped(db.Opportunities, ObjectKeys.Opportunity);

            // Grouped server-side; probability weighting is applied to the small grouped result.
            openGroups = (await opps.Where(o => o.ClosedAt == null)
                    .GroupBy(o => new { o.Stage, o.Probability })
                    .Select(g => new { g.Key.Stage, g.Key.Probability, Count = g.Count(), Amount = g.Sum(o => o.Amount) })
                    .ToListAsync(ct))
                .Select(g => (g.Stage, g.Probability, g.Count, g.Amount)).ToList();
            pipelineByStage = stages.Where(s => s.Category == StageCategory.Open)
                .Select(s => new StageSummary(s.Key, s.Label, s.Color,
                    openGroups.Where(g => g.Stage == s.Key).Sum(g => g.Count),
                    openGroups.Where(g => g.Stage == s.Key).Sum(g => g.Amount)))
                .ToList();

            var closedRecent = await opps.Where(o => o.ClosedAt != null && o.ClosedAt >= now.AddDays(-90)).Select(o => o.Stage).ToListAsync(ct);
            var won90 = closedRecent.Count(wonKeys.Contains);
            winRate = closedRecent.Count == 0 ? 0 : Math.Round(won90 * 100.0 / closedRecent.Count, 1);

            var wonSince = await opps.Where(o => wonKeys.Contains(o.Stage) && o.ClosedAt >= sixMonthsStart)
                .Select(o => new { o.Amount, ClosedAt = o.ClosedAt!.Value }).ToListAsync(ct);
            wonByMonth = Enumerable.Range(0, 6).Select(i => sixMonthsStart.AddMonths(i))
                .Select(m => new MonthlyWon(m.Year, m.Month,
                    wonSince.Count(o => o.ClosedAt.Year == m.Year && o.ClosedAt.Month == m.Month),
                    wonSince.Where(o => o.ClosedAt.Year == m.Year && o.ClosedAt.Month == m.Month).Sum(o => o.Amount)))
                .ToList();
            wonThisMonthCount = wonByMonth[^1].Count;
            wonThisMonthAmount = wonByMonth[^1].Amount;
            sections.Add(ObjectKeys.Opportunity);
        }

        if (Visible(ObjectKeys.Activity))
        {
            overdue = await Scoped(db.Activities, ObjectKeys.Activity).CountAsync(a => a.CompletedAt == null && a.DueAt < now, ct);
            upcoming = await ActivityEndpoints.Project(db.Activities.AsNoTracking()
                    .Where(a => a.OwnerId == me && a.CompletedAt == null && a.DueAt != null && a.DueAt <= now.AddDays(7))
                    .OrderBy(a => a.DueAt))
                .Take(10).ToListAsync(ct);
            sections.Add(ObjectKeys.Activity);
        }

        var customPipelines = new List<CustomPipelineSummary>();
        foreach (var definition in objects.Where(o => !o.IsStandard && o.IsActive && o.HasPipeline && p.Can(o.Key, Read)))
        {
            var stages = await meta.StagesAsync(definition.Key, ct);
            var counts = await Scoped(db.CustomRecords.Where(r => r.ObjectDefinitionId == definition.Id), definition.Key)
                .Where(r => r.ClosedAt == null)
                .GroupBy(r => r.Stage).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
            customPipelines.Add(new CustomPipelineSummary(definition.Key, definition.PluralLabel, definition.Icon, counts.Values.Sum(),
                stages.Where(s => s.Category == StageCategory.Open)
                    .Select(s => new StageSummary(s.Key, s.Label, s.Color, counts.GetValueOrDefault(s.Key), 0)).ToList()));
        }

        return TypedResults.Ok(new DashboardSummary(
            sections, accountCount, contactCount, openLeadCount,
            openGroups.Sum(g => g.Count),
            openGroups.Sum(g => g.Amount),
            Math.Round(openGroups.Sum(g => g.Amount * g.Probability / 100m), 2),
            wonThisMonthCount, wonThisMonthAmount, winRate, overdue,
            pipelineByStage, leadsBySource, wonByMonth, upcoming, customPipelines));
    }
}
