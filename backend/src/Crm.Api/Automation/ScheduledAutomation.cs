using System.Globalization;
using System.Reflection;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Automation;

/// <summary>
/// Runs "date reached" rules: each fires once per record when <c>date field + offset</c> passes. Dates that passed before the
/// rule existed, or longer ago than <c>Automation:ScheduleLookbackHours</c> (default 48), are skipped, so enabling a rule
/// never floods old records and downtime is caught up within the lookback window.
/// </summary>
public sealed class ScheduledAutomation(
    CrmDbContext db, TenantContext tenant, AutomationEngine engine, IConfiguration config, ILogger<ScheduledAutomation> logger)
{
    /// <summary>Upper bound on records scanned per rule and run; custom date fields live in JSON, so they're compared in memory.</summary>
    public const int MaxRecordsPerRule = 20000;

    private static readonly HashSet<Type> DateTypes = [typeof(DateTime), typeof(DateOnly)];

    /// <summary>Date fields a rule can be scheduled on: standard date properties plus custom Date/DateTime fields.</summary>
    public static IEnumerable<string> StandardDateFields(Type entityType) =>
        RecordFields.ScalarProperties(entityType)
            .Where(p => DateTypes.Contains(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType))
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]);

    /// <returns>How many rule runs matched and executed their actions.</returns>
    public async Task<int> RunDueAsync(DateTime now, CancellationToken ct)
    {
        var lookback = TimeSpan.FromHours(Math.Max(1, config.GetValue("Automation:ScheduleLookbackHours", 48)));
        var activeTenants = db.Tenants.Where(t => t.Status == TenantStatus.Active).Select(t => t.Id);
        var rules = await db.AutomationRules.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.IsActive && r.Trigger == AutomationTrigger.DateReached && r.ScheduleField != null && activeTenants.Contains(r.TenantId))
            .Select(r => new { r.Id, r.TenantId })
            .ToListAsync(ct);

        var fired = 0;
        foreach (var rule in rules)
        {
            using (tenant.Use(rule.TenantId, userName: "Automation"))
            {
                try
                {
                    fired += await RunRuleAsync(rule.Id, now, lookback, ct);
                }
                catch (DbUpdateException ex)
                {
                    // Another API instance handled the same due records first (unique RuleId + RecordId + DueAt).
                    logger.LogInformation(ex, "Scheduled rule {RuleId} was already handled elsewhere", rule.Id);
                }
                finally
                {
                    db.ChangeTracker.Clear();
                }
            }
        }
        return fired;
    }

    private async Task<int> RunRuleAsync(Guid ruleId, DateTime now, TimeSpan lookback, CancellationToken ct)
    {
        var rule = await db.AutomationRules.SingleAsync(r => r.Id == ruleId, ct);
        var field = rule.ScheduleField!;
        var offset = TimeSpan.FromMinutes(rule.ScheduleOffsetMinutes);
        var earliest = rule.CreatedAt > now - lookback ? rule.CreatedAt : now - lookback;

        var query = Query(rule.ObjectKey);
        if (field == "createdAt") query = query.Where(x => x.CreatedAt >= earliest - offset && x.CreatedAt <= now - offset);
        else if (field == "updatedAt") query = query.Where(x => x.UpdatedAt >= earliest - offset && x.UpdatedAt <= now - offset);
        var candidates = await query.AsNoTracking().OrderBy(x => x.Id).Take(MaxRecordsPerRule).ToListAsync(ct);
        if (candidates.Count == MaxRecordsPerRule)
            logger.LogWarning("Scheduled rule {RuleId} scanned the maximum of {Max} records; some may be checked late", rule.Id, MaxRecordsPerRule);

        var reader = DateReader(rule.ObjectKey, field);
        var due = candidates
            .Select(r => (Record: r, Due: reader(r) is { } date ? Seconds(date + offset) : (DateTime?)null))
            .Where(x => x.Due >= earliest && x.Due <= now)
            .ToList();
        if (due.Count == 0) return 0;

        var ids = due.Select(x => x.Record.Id).ToList();
        var done = (await db.ScheduledRuleRuns.Where(x => x.RuleId == rule.Id && ids.Contains(x.RecordId))
            .Select(x => new { x.RecordId, x.DueAt }).ToListAsync(ct))
            .Select(x => (x.RecordId, x.DueAt)).ToHashSet();
        var fieldDefs = await db.FieldDefinitions.Where(f => f.ObjectKey == rule.ObjectKey).ToListAsync(ct);

        var fired = 0;
        foreach (var (record, dueAt) in due.Where(x => !done.Contains((x.Record.Id, x.Due!.Value))))
        {
            db.Attach(record);
            var run = new AutomationEngine.RuleRun(record, rule.ObjectKey, RecordFields.Read(record), fieldDefs, now);
            var matched = await engine.RunRuleAsync(db, rule, run, before: null, ct);
            db.ScheduledRuleRuns.Add(new ScheduledRuleRun { RuleId = rule.Id, RecordId = record.Id, DueAt = dueAt!.Value, RanAt = now, Matched = matched });
            if (matched) fired++;
        }
        // Like save-triggered rules, changes made by actions don't trigger further rules.
        db.SuppressAutomation = true;
        try { await db.SaveChangesAsync(ct); }
        finally { db.SuppressAutomation = false; }
        return fired;
    }

    private IQueryable<OwnedEntity> Query(string objectKey) => objectKey switch
    {
        ObjectKeys.Account => db.Accounts,
        ObjectKeys.Contact => db.Contacts,
        ObjectKeys.Lead => db.Leads,
        ObjectKeys.Opportunity => db.Opportunities,
        ObjectKeys.Activity => db.Activities,
        _ => db.CustomRecords.Where(r => r.ObjectDefinition!.Key == objectKey),
    };

    private static Func<OwnedEntity, DateTime?> DateReader(string objectKey, string field)
    {
        if (field.StartsWith("cf.", StringComparison.Ordinal))
        {
            var key = field[3..];
            return r => ParseDate(Cf.AsText(Cf.Parse(r.CustomFields)[key]));
        }
        var property = RecordStore.EntityType(objectKey).GetProperty(char.ToUpperInvariant(field[0]) + field[1..], BindingFlags.Public | BindingFlags.Instance);
        return r => property?.GetValue(r) switch
        {
            DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            DateOnly d => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            _ => null,
        };
    }

    /// <summary>Custom Date values ("2026-10-05") are taken as midnight UTC; DateTime values keep their time.</summary>
    public static DateTime? ParseDate(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.UtcDateTime : null;

    /// <summary>Whole seconds, so the stored due time compares equal across database precisions.</summary>
    private static DateTime Seconds(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
}

/// <summary>Checks date-based rules every <c>Automation:ScheduleIntervalSeconds</c> (default 300).</summary>
public sealed class ScheduledAutomationService(
    IServiceScopeFactory scopes, TimeProvider clock, IConfiguration config, ILogger<ScheduledAutomationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(10, config.GetValue("Automation:ScheduleIntervalSeconds", 300)));
        using var timer = new PeriodicTimer(interval, clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var fired = await scope.ServiceProvider.GetRequiredService<ScheduledAutomation>().RunDueAsync(clock.GetUtcNow().UtcDateTime, stoppingToken);
                if (fired > 0) logger.LogInformation("Date-based automation ran {Count} time(s)", fired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Date-based automation run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
