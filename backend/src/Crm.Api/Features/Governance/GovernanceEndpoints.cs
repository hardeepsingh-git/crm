using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Crm.Api.Auth;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Platform;
using Crm.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Governance;

public record AuditItem(
    Guid Id, string EntityType, Guid EntityId, string? EntityName, AuditAction Action,
    Guid? UserId, string? UserName, DateTime Timestamp, JsonObject Changes);

public record AuditQuery(
    string? EntityType, Guid? EntityId, Guid? UserId, AuditAction? Action, string? Search, DateTime? From, DateTime? To,
    int Page = 1, int PageSize = 50);

public record SettingsRequest(
    [Required, MaxLength(200)] string Name,
    [Required, StringLength(3, MinimumLength = 3)] string Currency,
    bool? SelfRegistrationEnabled = null);

/// <summary>Organisation settings plus the platform switches that decide whether self-registration can work at all.</summary>
public record OrganisationSettings(
    Guid Id, string Name, string Slug, string Currency, string Template, bool SelfRegistrationEnabled,
    bool SelfRegistrationAllowedByPlatform, bool EmailVerification, bool SmsVerification);

public record NotificationItem(Guid Id, string Title, string Message, string? Link, DateTime CreatedAt, DateTime? ReadAt);

public record NotificationList(int UnreadCount, IReadOnlyList<NotificationItem> Items);

public static class GovernanceEndpoints
{
    public static RouteGroupBuilder MapGovernance(this RouteGroupBuilder api)
    {
        api.MapGet("/admin/audit", Search).WithTags("Audit").RequirePermission(AdminResources.Audit, PermissionActions.Read);
        api.MapGet("/audit/{entityType}/{entityId:guid}", RecordHistory).WithTags("Audit");

        api.MapGet("/settings", GetSettings).WithTags("Settings");
        api.MapPut("/settings", UpdateSettings).WithTags("Settings").RequirePermission(AdminResources.Settings, PermissionActions.Manage);

        var n = api.MapGroup("/notifications").WithTags("Notifications");
        n.MapGet("/", Notifications);
        n.MapPost("/{id:guid}/read", MarkRead);
        n.MapPost("/read-all", MarkAllRead);
        return api;
    }

    private static async Task<Ok<PagedResult<AuditItem>>> Search([AsParameters] AuditQuery q, CrmDbContext db, CancellationToken ct)
    {
        var query = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.EntityType)) query = query.Where(a => a.EntityType == q.EntityType);
        if (q.EntityId is not null) query = query.Where(a => a.EntityId == q.EntityId);
        if (q.UserId is not null) query = query.Where(a => a.UserId == q.UserId);
        if (q.Action is not null) query = query.Where(a => a.Action == q.Action);
        if (QueryableExtensions.NormalizeSearch(q.Search) is { } search)
            query = query.Where(a => a.EntityName != null && a.EntityName.ToLower().Contains(search));
        if (q.From is not null) query = query.Where(a => a.Timestamp >= q.From);
        if (q.To is not null) query = query.Where(a => a.Timestamp <= q.To);
        return TypedResults.Ok(await Project(query.OrderByDescending(a => a.Timestamp)).ToPagedAsync(q.Page, q.PageSize, ct));
    }

    /// <summary>Change history of one record, for anyone allowed to read that record.</summary>
    private static async Task<Results<Ok<List<AuditItem>>, NotFound>> RecordHistory(
        string entityType, Guid entityId, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var owner = await RecordOwners.OwnerOfAsync(db, entityType, entityId, ct);
        if (owner is null || !p.CanAccess(entityType, PermissionActions.Read, owner.Value)) return TypedResults.NotFound();
        return TypedResults.Ok(await Project(db.AuditLogs.AsNoTracking().Where(a => a.EntityId == entityId).OrderByDescending(a => a.Timestamp))
            .Take(200).ToListAsync(ct));
    }

    private static IQueryable<AuditItem> Project(IQueryable<AuditLog> q) => q.Select(a => new AuditItem(
        a.Id, a.EntityType, a.EntityId, a.EntityName, a.Action, a.UserId, a.UserName, a.Timestamp, Cf.Parse(a.Changes)));

    private static async Task<Ok<OrganisationSettings>> GetSettings(Access access, CrmDbContext db, PlatformSettingsService platform, CancellationToken ct)
    {
        var t = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == access.TenantId, ct);
        return TypedResults.Ok(ToSettings(t, await platform.GetAsync(ct)));
    }

    private static async Task<Ok<OrganisationSettings>> UpdateSettings(
        SettingsRequest req, Access access, CrmDbContext db, PlatformSettingsService platform, CancellationToken ct)
    {
        var t = await db.Tenants.SingleAsync(x => x.Id == access.TenantId, ct);
        t.Name = req.Name.Trim();
        t.Currency = req.Currency.ToUpperInvariant();
        if (req.SelfRegistrationEnabled is { } enabled) t.SelfRegistrationEnabled = enabled;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToSettings(t, await platform.GetAsync(ct)));
    }

    private static OrganisationSettings ToSettings(Tenant t, PlatformSettings p) => new(
        t.Id, t.Name, t.Slug, t.Currency, t.Template, t.SelfRegistrationEnabled,
        p.SelfRegistrationEnabled, p.EmailVerificationEnabled, p.SmsVerificationEnabled);

    private static async Task<Ok<NotificationList>> Notifications(Access access, CrmDbContext db, CancellationToken ct)
    {
        var mine = db.Notifications.AsNoTracking().Where(n => n.UserId == access.UserId);
        var unread = await mine.CountAsync(n => n.ReadAt == null, ct);
        var items = await mine.OrderBy(n => n.ReadAt != null).ThenByDescending(n => n.CreatedAt).Take(50)
            .Select(n => new NotificationItem(n.Id, n.Title, n.Message, n.Link, n.CreatedAt, n.ReadAt)).ToListAsync(ct);
        return TypedResults.Ok(new NotificationList(unread, items));
    }

    private static async Task<Results<NoContent, NotFound>> MarkRead(Guid id, Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var n = await db.Notifications.SingleOrDefaultAsync(x => x.Id == id && x.UserId == access.UserId, ct);
        if (n is null) return TypedResults.NotFound();
        n.ReadAt ??= clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> MarkAllRead(Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await db.Notifications.Where(n => n.UserId == access.UserId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
        return TypedResults.NoContent();
    }
}
