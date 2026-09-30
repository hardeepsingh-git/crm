using Crm.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Crm.Api.Data;

public class CrmDbContext(DbContextOptions<CrmDbContext> options, TimeProvider clock)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(CrmDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<decimal>().HavePrecision(18, 2);
        builder.Properties<LeadSource>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<LeadStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<OpportunityStage>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<ActivityType>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<ActivityPriority>().HaveConversion<string>().HaveMaxLength(32);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}

// PostgreSQL timestamptz requires UTC; normalise anything else on the way in.
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
