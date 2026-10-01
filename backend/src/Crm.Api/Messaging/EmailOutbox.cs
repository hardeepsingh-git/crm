using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Messaging;

/// <summary>Wakes the dispatcher as soon as emails are queued, so they don't wait for the next poll.</summary>
public sealed class OutboxSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>Delivers due outbox emails. Safe to run on several API instances: each email is claimed with a short lease first.</summary>
public sealed class EmailDispatcher(CrmDbContext db, TenantContext tenant, IEmailSender sender, ILogger<EmailDispatcher> logger)
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)];

    /// <returns>How many emails were processed (sent, rescheduled or failed).</returns>
    public async Task<int> DispatchDueAsync(DateTime now, CancellationToken ct, int batchSize = 50)
    {
        // Suspended tenants' mail waits; it's sent if they are reactivated, or removed with the tenant.
        var activeTenants = db.Tenants.Where(t => t.Status == TenantStatus.Active).Select(t => t.Id);
        var due = await db.OutboundEmails.IgnoreQueryFilters()
            .Where(e => e.Status == OutboundEmailStatus.Pending && e.NextAttemptAt <= now
                        && (e.LockedUntil == null || e.LockedUntil < now) && activeTenants.Contains(e.TenantId))
            .OrderBy(e => e.NextAttemptAt)
            .Select(e => e.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        var processed = 0;
        foreach (var id in due)
        {
            var leaseEnd = now + Lease;
            var claimed = await db.OutboundEmails.IgnoreQueryFilters()
                .Where(e => e.Id == id && e.Status == OutboundEmailStatus.Pending && (e.LockedUntil == null || e.LockedUntil < now))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.LockedUntil, leaseEnd), ct);
            if (claimed == 0) continue;

            var email = await db.OutboundEmails.IgnoreQueryFilters().SingleAsync(e => e.Id == id, ct);
            using (tenant.Use(email.TenantId, userName: "System"))
            {
                await DeliverAsync(email, now, ct);
                db.SuppressAutomation = true;
                try { await db.SaveChangesAsync(ct); }
                finally { db.SuppressAutomation = false; }
            }
            db.ChangeTracker.Clear();
            processed++;
        }
        return processed;
    }

    private async Task DeliverAsync(OutboundEmail email, DateTime now, CancellationToken ct)
    {
        email.Attempts++;
        email.LockedUntil = null;
        try
        {
            await sender.SendAsync(new EmailMessage(email.To, email.Subject, email.Body, email.ReplyTo), ct);
            email.Status = OutboundEmailStatus.Sent;
            email.SentAt = now;
            email.LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            email.LastError = ex.Message.Length <= 1000 ? ex.Message : ex.Message[..1000];
            // A malformed address will never work; anything else (network, SMTP 4xx/5xx) is retried with backoff.
            var permanent = ex is FormatException or ArgumentException;
            if (permanent || email.Attempts >= MaxAttempts)
            {
                email.Status = OutboundEmailStatus.Failed;
                await MarkNotSentAsync(email, ct);
                logger.LogWarning(ex, "Email {EmailId} failed permanently after {Attempts} attempt(s)", email.Id, email.Attempts);
            }
            else
            {
                email.NextAttemptAt = now + Backoff[Math.Min(email.Attempts - 1, Backoff.Length - 1)];
                logger.LogWarning(ex, "Email {EmailId} attempt {Attempts} failed; retrying at {Next}", email.Id, email.Attempts, email.NextAttemptAt);
            }
        }
    }

    /// <summary>Makes the timeline honest: the Email activity logged when it was queued now says it wasn't sent.</summary>
    public async Task MarkNotSentAsync(OutboundEmail email, CancellationToken ct)
    {
        if (email.ActivityId is null) return;
        var activity = await db.Activities.FirstOrDefaultAsync(a => a.Id == email.ActivityId, ct);
        if (activity is null || activity.Subject.StartsWith(NotSent, StringComparison.Ordinal)) return;
        activity.Subject = Truncate(NotSent + activity.Subject, 200);
        activity.Description = Truncate($"Not sent: {email.LastError}\n\n{activity.Description}", 4000);
    }

    public const string NotSent = "[Not sent] ";

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>Runs the dispatcher every <c>Email:DispatchIntervalSeconds</c> (default 15), or immediately when signalled.</summary>
public sealed class EmailDispatcherService(
    IServiceScopeFactory scopes, OutboxSignal signal, TimeProvider clock, IConfiguration config, ILogger<EmailDispatcherService> logger)
    : BackgroundService
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, config.GetValue("Email:DispatchIntervalSeconds", 15)));
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                processed = await scope.ServiceProvider.GetRequiredService<EmailDispatcher>()
                    .DispatchDueAsync(clock.GetUtcNow().UtcDateTime, stoppingToken, BatchSize);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Email dispatch run failed");
            }
            if (processed == BatchSize) continue;
            try { await signal.WaitAsync(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
