using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Platform;

public record TenantItem(
    Guid Id, string Name, string Slug, TenantStatus Status, string Currency, string Template, DateTime CreatedAt, int UserCount);

public record CreateTenantRequest(
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(64)] string Slug,
    [Required] string Template,
    [Required, StringLength(3, MinimumLength = 3)] string Currency,
    [Required, EmailAddress, MaxLength(256)] string AdminEmail,
    [Required, MaxLength(100)] string AdminFirstName,
    [Required, MaxLength(100)] string AdminLastName,
    [Required] string AdminPassword,
    bool SeedDemoData);

public record UpdateTenantRequest(
    [Required, MaxLength(200)] string Name,
    [Required, StringLength(3, MinimumLength = 3)] string Currency,
    TenantStatus Status);

public sealed partial class TenantService(
    CrmDbContext db, UserManager<AppUser> users, TenantContext tenantContext, TimeProvider clock, ILogger<TenantService> logger)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
    private static partial Regex SlugPattern();

    public async Task<(Tenant? Tenant, Dictionary<string, string[]> Errors)> CreateAsync(CreateTenantRequest req, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var slug = req.Slug.Trim().ToLowerInvariant();
        if (!SlugPattern().IsMatch(slug)) errors["slug"] = ["Use 2-63 lowercase letters, digits or hyphens."];
        else if (await db.Tenants.AnyAsync(t => t.Slug == slug, ct)) errors["slug"] = ["This slug is already taken."];
        if (TenantTemplates.All.All(t => t.Key != req.Template)) errors["template"] = ["Unknown template."];
        if (await users.FindByEmailAsync(req.AdminEmail) is not null) errors["adminEmail"] = ["A user with this email already exists."];

        var admin = new AppUser
        {
            UserName = req.AdminEmail.Trim(), Email = req.AdminEmail.Trim(), EmailConfirmed = true,
            FirstName = req.AdminFirstName.Trim(), LastName = req.AdminLastName.Trim(),
        };
        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, admin, req.AdminPassword);
            if (!result.Succeeded) errors["adminPassword"] = result.Errors.Select(e => e.Description).ToArray();
        }
        if (errors.Count > 0) return (null, errors);

        var tenant = new Tenant
        {
            Name = req.Name.Trim(), Slug = slug, Template = req.Template, Currency = req.Currency.ToUpperInvariant(),
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);

        using (tenantContext.Use(tenant.Id))
        {
            await TenantTemplates.ApplyAsync(db, req.Template, ct);
            admin.TenantId = tenant.Id;
            var created = await users.CreateAsync(admin, req.AdminPassword);
            if (!created.Succeeded)
            {
                await tx.RollbackAsync(ct);
                return (null, new() { ["adminEmail"] = created.Errors.Select(e => e.Description).ToArray() });
            }
            var adminRole = await db.Roles.SingleAsync(r => r.IsAdministrator, ct);
            db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = adminRole.Id });
            await db.SaveChangesAsync(ct);

            if (req.SeedDemoData) await DemoData.SeedAsync(db, users, admin, null, logger, ct);
        }
        await tx.CommitAsync(ct);
        logger.LogInformation("Provisioned tenant {Slug} from template {Template}", tenant.Slug, tenant.Template);
        return (tenant, errors);
    }

    /// <summary>Permanently removes a tenant and all of its data, children before parents.</summary>
    public async Task DeleteAsync(Guid tenantId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var userIds = await db.Users.Where(u => u.TenantId == tenantId).Select(u => u.Id).ToListAsync(ct);
        var roleIds = await db.Roles.IgnoreQueryFilters().Where(r => r.TenantId == tenantId).Select(r => r.Id).ToListAsync(ct);
        var groupIds = await db.Groups.IgnoreQueryFilters().Where(g => g.TenantId == tenantId).Select(g => g.Id).ToListAsync(ct);

        await db.Activities.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await db.CustomRecords.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await db.Leads.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await db.Opportunities.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await db.Contacts.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await db.Accounts.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await db.AutomationRules.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await db.FieldDefinitions.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await db.PipelineStages.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await db.Picklists.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await db.ObjectDefinitions.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await db.Notifications.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await db.AuditLogs.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await db.GroupMembers.Where(x => groupIds.Contains(x.GroupId)).ExecuteDeleteAsync(ct);
        await db.GroupRoles.Where(x => groupIds.Contains(x.GroupId)).ExecuteDeleteAsync(ct);
        await db.Groups.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await db.UserRoles.Where(x => roleIds.Contains(x.RoleId)).ExecuteDeleteAsync(ct);
        await db.RolePermissions.Where(x => roleIds.Contains(x.RoleId)).ExecuteDeleteAsync(ct);
        await db.Roles.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await db.RefreshTokens.Where(x => userIds.Contains(x.UserId)).ExecuteDeleteAsync(ct);
        await db.Users.Where(u => u.TenantId == tenantId).ExecuteDeleteAsync(ct);
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        PermissionService.Invalidate(tenantId);
        logger.LogWarning("Deleted tenant {TenantId} and all of its data", tenantId);
    }
}

public static class TenantEndpoints
{
    public static RouteGroupBuilder MapPlatform(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/platform").WithTags("Platform").RequirePlatformAdmin();
        g.MapGet("/templates", () => TypedResults.Ok(TenantTemplates.All));
        g.MapGet("/tenants", List);
        g.MapPost("/tenants", Create);
        g.MapPut("/tenants/{id:guid}", Update);
        g.MapDelete("/tenants/{id:guid}", Delete);
        return api;
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> Delete(
        Guid id, CrmDbContext db, TenantService tenants, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return TypedResults.NotFound();
        if (id == Tenant.DefaultId) return Errors.Validation("id", "The default tenant cannot be deleted.");
        if (tenant.Status != TenantStatus.Suspended) return Errors.Validation("status", "Suspend the tenant before deleting it.");
        await tenants.DeleteAsync(id, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<TenantItem>>> List(CrmDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Tenants.AsNoTracking().OrderBy(t => t.Name)
            .Select(t => new TenantItem(t.Id, t.Name, t.Slug, t.Status, t.Currency, t.Template, t.CreatedAt,
                db.Users.Count(u => u.TenantId == t.Id)))
            .ToListAsync(ct));

    private static async Task<Results<Created<TenantItem>, ValidationProblem>> Create(CreateTenantRequest req, TenantService tenants, CancellationToken ct)
    {
        var (tenant, errors) = await tenants.CreateAsync(req, ct);
        if (tenant is null) return Errors.Validation(errors);
        return TypedResults.Created($"/api/platform/tenants/{tenant.Id}",
            new TenantItem(tenant.Id, tenant.Name, tenant.Slug, tenant.Status, tenant.Currency, tenant.Template, tenant.CreatedAt, 1));
    }

    private static async Task<Results<Ok<TenantItem>, NotFound, ValidationProblem>> Update(
        Guid id, UpdateTenantRequest req, CrmDbContext db, Access access, CancellationToken ct)
    {
        var tenant = await db.Tenants.FindAsync([id], ct);
        if (tenant is null) return TypedResults.NotFound();
        if (id == access.TenantId && req.Status != TenantStatus.Active)
            return Errors.Validation("status", "You cannot suspend your own tenant.");

        tenant.Name = req.Name.Trim();
        tenant.Currency = req.Currency.ToUpperInvariant();
        tenant.Status = req.Status;
        await db.SaveChangesAsync(ct);
        PermissionService.Invalidate(id);

        var userCount = await db.Users.CountAsync(u => u.TenantId == id, ct);
        return TypedResults.Ok(new TenantItem(tenant.Id, tenant.Name, tenant.Slug, tenant.Status, tenant.Currency, tenant.Template, tenant.CreatedAt, userCount));
    }
}
