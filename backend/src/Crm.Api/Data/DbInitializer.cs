using Crm.Api.Common;
using Crm.Api.Domain;
using Crm.Api.Platform;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();

        switch (config["Database:Strategy"] ?? "Migrate")
        {
            case "Migrate":
                await db.Database.MigrateAsync();
                break;
            case "EnsureCreated":
                await db.Database.EnsureCreatedAsync();
                break;
            default:
                return;
        }

        if (!await db.Tenants.AnyAsync(t => t.Id == Tenant.DefaultId))
        {
            db.Tenants.Add(new Tenant
            {
                Id = Tenant.DefaultId, Name = config["Seed:TenantName"] ?? "Default", Slug = "default",
                Template = TenantTemplates.Sales, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await scope.ServiceProvider.GetRequiredService<PlatformSettingsService>().EnsureDefaultsAsync();

        var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
        using var _ = tenantContext.Use(Tenant.DefaultId, userName: "System");

        // Tenants created before configuration existed (including upgraded single-tenant installs) get the sales template.
        if (!await db.ObjectDefinitions.AnyAsync())
        {
            await TenantTemplates.ApplyAsync(db, TenantTemplates.Sales);
            logger.LogInformation("Applied the sales template to the default tenant");
        }
        else
        {
            await TenantTemplates.EnsureRolesAsync(db, default);
            await db.SaveChangesAsync();
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var admin = await EnsurePlatformAdminAsync(db, users, config, logger);

        if (admin is not null && config.GetValue<bool>("Seed:DemoData") && !await db.Accounts.AnyAsync())
            await DemoData.SeedAsync(db, users, admin, config["Seed:DemoUserPassword"], logger);

        await TenantTemplates.UpgradeTenantsAsync(db, tenantContext, logger);
    }

    private static async Task<AppUser?> EnsurePlatformAdminAsync(CrmDbContext db, UserManager<AppUser> users, IConfiguration config, ILogger logger)
    {
        var existing = await db.Users.Where(u => u.IsPlatformAdmin).OrderBy(u => u.CreatedAt).FirstOrDefaultAsync();
        if (existing is not null) return existing.TenantId == Tenant.DefaultId ? existing : null;

        var email = config["Seed:AdminEmail"];
        var password = config["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("No platform administrator exists. Set Seed:AdminEmail and Seed:AdminPassword to create one.");
            return null;
        }

        var admin = new AppUser
        {
            UserName = email, Email = email, EmailConfirmed = true, TenantId = Tenant.DefaultId,
            FirstName = "System", LastName = "Administrator", IsPlatformAdmin = true,
        };
        var result = await users.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            logger.LogError("Failed to create admin user: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
            return null;
        }
        var role = await db.Roles.SingleAsync(r => r.IsAdministrator);
        db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        logger.LogInformation("Created platform administrator {Email}", email);
        return admin;
    }
}
