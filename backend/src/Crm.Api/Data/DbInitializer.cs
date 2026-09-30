using Crm.Api.Common;
using Crm.Api.Domain;
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

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in Roles.All)
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole<Guid>(role));

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var admin = await EnsureAdminAsync(users, config, logger);

        if (admin is not null && config.GetValue<bool>("Seed:DemoData") && !await db.Accounts.AnyAsync())
        {
            await DemoData.SeedAsync(db, users, admin, config["Seed:DemoUserPassword"], logger);
        }
    }

    private static async Task<AppUser?> EnsureAdminAsync(UserManager<AppUser> users, IConfiguration config, ILogger logger)
    {
        var existing = await users.GetUsersInRoleAsync(Roles.Admin);
        if (existing.Count > 0) return existing[0];

        var email = config["Seed:AdminEmail"];
        var password = config["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("No admin user exists. Set Seed:AdminEmail and Seed:AdminPassword to create one.");
            return null;
        }

        var admin = new AppUser
        {
            UserName = email, Email = email, EmailConfirmed = true,
            FirstName = "System", LastName = "Administrator",
        };
        var result = await users.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            logger.LogError("Failed to create admin user: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
            return null;
        }
        await users.AddToRoleAsync(admin, Roles.Admin);
        logger.LogInformation("Created admin user {Email}", email);
        return admin;
    }
}
