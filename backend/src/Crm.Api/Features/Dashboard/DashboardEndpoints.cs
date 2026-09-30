using System.Security.Claims;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Features.Activities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Dashboard;

public record StageSummary(OpportunityStage Stage, int Count, decimal Amount);

public record SourceSummary(LeadSource Source, int Count);

public record MonthlyWon(int Year, int Month, int Count, decimal Amount);

public record DashboardSummary(
    int AccountCount, int ContactCount, int OpenLeadCount, int OpenOpportunityCount,
    decimal OpenPipelineAmount, decimal WeightedPipelineAmount,
    int WonThisMonthCount, decimal WonThisMonthAmount, double WinRate, int OverdueActivityCount,
    IReadOnlyList<StageSummary> PipelineByStage, IReadOnlyList<SourceSummary> LeadsBySource,
    IReadOnlyList<MonthlyWon> WonByMonth, IReadOnlyList<ActivityItem> UpcomingActivities);

public static class DashboardEndpoints
{
    private static readonly OpportunityStage[] OpenStages =
        [OpportunityStage.Prospecting, OpportunityStage.Qualification, OpportunityStage.Proposal, OpportunityStage.Negotiation];

    public static RouteGroupBuilder MapDashboard(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", Get).WithTags("Dashboard");
        return api;
    }

    private static async Task<Ok<DashboardSummary>> Get(
        ClaimsPrincipal user, CrmDbContext db, TimeProvider clock, CancellationToken ct, bool mine = false)
    {
        var me = user.GetUserId();
        var now = clock.GetUtcNow().UtcDateTime;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var sixMonthsStart = monthStart.AddMonths(-5);

        var accounts = db.Accounts.AsNoTracking();
        var contacts = db.Contacts.AsNoTracking();
        var leads = db.Leads.AsNoTracking();
        var opps = db.Opportunities.AsNoTracking();
        var activities = db.Activities.AsNoTracking();
        if (mine)
        {
            accounts = accounts.Where(x => x.OwnerId == me);
            contacts = contacts.Where(x => x.OwnerId == me);
            leads = leads.Where(x => x.OwnerId == me);
            opps = opps.Where(x => x.OwnerId == me);
            activities = activities.Where(x => x.OwnerId == me);
        }

        var accountCount = await accounts.CountAsync(ct);
        var contactCount = await contacts.CountAsync(ct);
        var openLeadCount = await leads.CountAsync(l => l.Status != LeadStatus.Converted && l.Status != LeadStatus.Unqualified, ct);

        // Grouped server-side; weighting is applied to the small grouped result.
        var openGroups = await opps.Where(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost)
            .GroupBy(o => new { o.Stage, o.Probability })
            .Select(g => new { g.Key.Stage, g.Key.Probability, Count = g.Count(), Amount = g.Sum(o => o.Amount) })
            .ToListAsync(ct);

        var pipelineByStage = OpenStages
            .Select(s => new StageSummary(s,
                openGroups.Where(g => g.Stage == s).Sum(g => g.Count),
                openGroups.Where(g => g.Stage == s).Sum(g => g.Amount)))
            .ToList();

        var closedRecent = await opps
            .Where(o => (o.Stage == OpportunityStage.ClosedWon || o.Stage == OpportunityStage.ClosedLost) && o.ClosedAt >= now.AddDays(-90))
            .Select(o => new { o.Stage, o.Amount, o.ClosedAt })
            .ToListAsync(ct);
        var won90 = closedRecent.Count(o => o.Stage == OpportunityStage.ClosedWon);
        var winRate = closedRecent.Count == 0 ? 0 : Math.Round(won90 * 100.0 / closedRecent.Count, 1);

        var wonSince = await opps
            .Where(o => o.Stage == OpportunityStage.ClosedWon && o.ClosedAt >= sixMonthsStart)
            .Select(o => new { o.Amount, ClosedAt = o.ClosedAt!.Value })
            .ToListAsync(ct);
        var wonByMonth = Enumerable.Range(0, 6)
            .Select(i => sixMonthsStart.AddMonths(i))
            .Select(m => new MonthlyWon(m.Year, m.Month,
                wonSince.Count(o => o.ClosedAt.Year == m.Year && o.ClosedAt.Month == m.Month),
                wonSince.Where(o => o.ClosedAt.Year == m.Year && o.ClosedAt.Month == m.Month).Sum(o => o.Amount)))
            .ToList();
        var thisMonth = wonByMonth[^1];

        var leadsBySource = (await leads.GroupBy(l => l.Source).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .Select(g => new SourceSummary(g.Key, g.Count))
            .OrderByDescending(s => s.Count)
            .ToList();

        var overdue = await activities.CountAsync(a => a.CompletedAt == null && a.DueAt < now, ct);

        var upcoming = await ActivityEndpoints.Project(db.Activities.AsNoTracking()
                .Where(a => a.OwnerId == me && a.CompletedAt == null && a.DueAt != null && a.DueAt <= now.AddDays(7))
                .OrderBy(a => a.DueAt))
            .Take(10)
            .ToListAsync(ct);

        return TypedResults.Ok(new DashboardSummary(
            accountCount, contactCount, openLeadCount,
            openGroups.Sum(g => g.Count),
            openGroups.Sum(g => g.Amount),
            Math.Round(openGroups.Sum(g => g.Amount * g.Probability / 100m), 2),
            thisMonth.Count, thisMonth.Amount, winRate, overdue,
            pipelineByStage, leadsBySource, wonByMonth, upcoming));
    }
}
