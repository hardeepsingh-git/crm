using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Crm.Api.Automation;
using Crm.Api.Common;
using Crm.Api.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Crm.Api.Data;

public class CrmDbContext(DbContextOptions<CrmDbContext> options, TimeProvider clock, TenantContext tenant, IServiceProvider services)
    : IdentityUserContext<AppUser, Guid>(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<CustomRecord> CustomRecords => Set<CustomRecord>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ObjectDefinition> ObjectDefinitions => Set<ObjectDefinition>();
    public DbSet<FieldDefinition> FieldDefinitions => Set<FieldDefinition>();
    public DbSet<PipelineStage> PipelineStages => Set<PipelineStage>();
    public DbSet<Picklist> Picklists => Set<Picklist>();
    public DbSet<AutomationRule> AutomationRules => Set<AutomationRule>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<GroupRole> GroupRoles => Set<GroupRole>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<VerificationCode> VerificationCodes => Set<VerificationCode>();

    /// <summary>Referenced by the global query filters; EF evaluates it per query.</summary>
    public Guid? CurrentTenantId => tenant.TenantId;

    public TenantContext TenantContext => tenant;

    private bool _runningAutomation;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(CrmDbContext).Assembly);

        // Users are resolved across tenants at sign-in, so they are scoped explicitly in queries instead.
        foreach (var entityType in builder.Model.GetEntityTypes()
                     .Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType) && t.ClrType != typeof(AppUser) && t.BaseType is null))
        {
            var e = Expression.Parameter(entityType.ClrType, "e");
            var body = Expression.Equal(
                Expression.Convert(Expression.Property(e, nameof(ITenantEntity.TenantId)), typeof(Guid?)),
                Expression.Property(Expression.Constant(this), nameof(CurrentTenantId)));
            builder.Entity(entityType.ClrType).HasQueryFilter(Expression.Lambda(body, e));
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<decimal>().HavePrecision(18, 2);
        builder.Properties<LeadStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<ActivityType>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<ActivityPriority>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<TenantStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<PermissionScope>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<FieldType>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<StageCategory>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<AutomationTrigger>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<AuditAction>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<RegistrationStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<VerificationChannel>().HaveConversion<string>().HaveMaxLength(16);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        StampAndGuard(now);
        var automationChanges = _runningAutomation ? [] : CaptureAutomationChanges();
        await WriteAuditAsync(now, cancellationToken);

        var result = await base.SaveChangesAsync(cancellationToken);

        if (automationChanges.Count > 0 && services.GetService(typeof(AutomationEngine)) is AutomationEngine engine)
        {
            _runningAutomation = true;
            try
            {
                if (await engine.RunAsync(this, automationChanges, cancellationToken))
                    result += await SaveChangesAsync(cancellationToken);
            }
            finally
            {
                _runningAutomation = false;
            }
        }
        return result;
    }

    private void StampAndGuard(DateTime now)
    {
        foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.TenantId == Guid.Empty)
                        entry.Entity.TenantId = CurrentTenantId ?? throw new InvalidOperationException($"No tenant context for new {entry.Entity.GetType().Name}.");
                    else if (CurrentTenantId is { } current && entry.Entity.TenantId != current)
                        throw new InvalidOperationException("Cross-tenant write rejected.");
                    break;
                case EntityState.Modified or EntityState.Deleted:
                    // Compare values: Identity's UpdateAsync flags every property as modified.
                    var tenantProperty = entry.Property(nameof(ITenantEntity.TenantId));
                    if (!Equals(tenantProperty.OriginalValue, tenantProperty.CurrentValue) ||
                        (CurrentTenantId is { } t && entry.Entity.TenantId != t))
                        throw new InvalidOperationException("Cross-tenant write rejected.");
                    break;
            }
        }

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
    }

    private List<RecordChange> CaptureAutomationChanges() =>
        ChangeTracker.Entries<OwnedEntity>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => new RecordChange(
                e.Entity,
                e.State == EntityState.Modified ? e.OriginalValues.ToObject() : null,
                e.State == EntityState.Added,
                e.Entity is IStaged && e.State == EntityState.Modified && e.Property(nameof(IStaged.Stage)).IsModified))
            .ToList();

    // ---------- Audit ----------

    private static readonly HashSet<string> NeverAudited =
        [nameof(AuditableEntity.Id), nameof(ITenantEntity.TenantId), nameof(AuditableEntity.CreatedAt), nameof(AuditableEntity.UpdatedAt),
         nameof(AutomationRule.RunCount), nameof(AutomationRule.LastRunAt)];

    private static readonly HashSet<string> AuditedUserProperties =
        [nameof(AppUser.Email), nameof(AppUser.FirstName), nameof(AppUser.LastName), nameof(AppUser.IsActive)];

    private async Task WriteAuditAsync(DateTime now, CancellationToken ct)
    {
        var entries = ChangeTracker.Entries<ITenantEntity>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                        && e.Entity is not AuditLog and not Notification)
            .ToList();
        if (entries.Count == 0) return;

        var definitionIds = entries.Select(e => e.Entity).OfType<CustomRecord>().Select(r => r.ObjectDefinitionId).Distinct().ToList();
        var objectKeys = definitionIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await ObjectDefinitions.IgnoreQueryFilters().Where(d => definitionIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Key, ct);

        foreach (var entry in entries)
        {
            var changes = new JsonObject();
            foreach (var prop in entry.Properties)
            {
                var name = prop.Metadata.Name;
                if (NeverAudited.Contains(name) || prop.Metadata.IsShadowProperty()) continue;
                if (entry.Entity is AppUser && !AuditedUserProperties.Contains(name)) continue;
                if (entry.State == EntityState.Deleted) break;
                if (entry.State == EntityState.Modified && !prop.IsModified) continue;

                var before = entry.State == EntityState.Added ? null : prop.OriginalValue;
                if (entry.State == EntityState.Modified && Equals(before, prop.CurrentValue)) continue;

                if (name == nameof(IHasCustomFields.CustomFields))
                    DiffCustomFields(changes, before as string, prop.CurrentValue as string);
                else
                    changes[Camel(name)] = new JsonArray(ToNode(before), ToNode(prop.CurrentValue));
            }
            if (entry.State == EntityState.Modified && changes.Count == 0) continue;

            AuditLogs.Add(new AuditLog
            {
                TenantId = entry.Entity.TenantId,
                EntityType = EntityTypeKey(entry.Entity, objectKeys),
                EntityId = (Guid)entry.Property("Id").CurrentValue!,
                EntityName = DisplayName(entry.Entity),
                Action = entry.State switch { EntityState.Added => AuditAction.Created, EntityState.Deleted => AuditAction.Deleted, _ => AuditAction.Updated },
                UserId = tenant.UserId,
                UserName = tenant.UserName ?? (_runningAutomation ? "Automation" : "System"),
                Timestamp = now,
                Changes = changes.ToJsonString(),
            });
        }
    }

    private static void DiffCustomFields(JsonObject changes, string? before, string? after)
    {
        var a = Cf.Parse(before);
        var b = Cf.Parse(after);
        foreach (var key in a.Select(p => p.Key).Union(b.Select(p => p.Key)))
        {
            var oldValue = a[key];
            var newValue = b[key];
            if (!JsonNode.DeepEquals(oldValue, newValue))
                changes[$"cf.{key}"] = new JsonArray(oldValue?.DeepClone(), newValue?.DeepClone());
        }
    }

    private static JsonNode? ToNode(object? value) => value is null ? null : JsonSerializer.SerializeToNode(value, AuditJson);

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    public static string EntityTypeKey(object entity, IReadOnlyDictionary<Guid, string>? customKeys = null) => entity switch
    {
        Account => ObjectKeys.Account,
        Contact => ObjectKeys.Contact,
        Lead => ObjectKeys.Lead,
        Opportunity => ObjectKeys.Opportunity,
        Activity => ObjectKeys.Activity,
        CustomRecord r => customKeys?.GetValueOrDefault(r.ObjectDefinitionId) ?? r.ObjectDefinition?.Key ?? "record",
        AppUser => "user",
        Role => "role",
        Group => "group",
        ObjectDefinition => "object_definition",
        FieldDefinition => "field_definition",
        PipelineStage => "pipeline_stage",
        Picklist => "picklist",
        AutomationRule => "automation_rule",
        _ => entity.GetType().Name,
    };

    public static string? DisplayName(object entity) => entity switch
    {
        Account a => a.Name,
        Contact c => $"{c.FirstName} {c.LastName}",
        Lead l => $"{l.FirstName} {l.LastName}",
        Opportunity o => o.Name,
        Activity a => a.Subject,
        CustomRecord r => r.Name,
        AppUser u => u.Email,
        Role r => r.Name,
        Group g => g.Name,
        ObjectDefinition d => d.Label,
        FieldDefinition f => $"{f.ObjectKey}.{f.Key}",
        PipelineStage s => $"{s.ObjectKey}.{s.Key}",
        Picklist p => p.Key,
        AutomationRule r => r.Name,
        _ => null,
    };
}

// PostgreSQL timestamptz requires UTC; normalise anything else on the way in.
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
