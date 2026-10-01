using Microsoft.AspNetCore.Identity;

namespace Crm.Api.Domain;

/// <summary>Rows that belong to exactly one tenant. Isolation is enforced centrally by the DbContext.</summary>
public interface ITenantEntity
{
    Guid TenantId { get; set; }
}

/// <summary>Entities carrying tenant-defined custom field values as a JSON object.</summary>
public interface IHasCustomFields
{
    string CustomFields { get; set; }
}

public class Tenant
{
    public static readonly Guid DefaultId = Guid.Parse("0199a000-0000-7000-8000-000000000001");

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public string Currency { get; set; } = "AUD";
    public string Template { get; set; } = "sales";
    /// <summary>Template revision this tenant's configuration is at; startup applies newer revisions once, additively.</summary>
    public int TemplateVersion { get; set; } = 1;
    /// <summary>Tenant opt-in; also requires the platform-wide switch in <see cref="AppSetting"/>.</summary>
    public bool SelfRegistrationEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AppUser : IdentityUser<Guid>, ITenantEntity
{
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    /// <summary>Can manage tenants across the whole platform.</summary>
    public bool IsPlatformAdmin { get; set; }
    public RegistrationStatus RegistrationStatus { get; set; } = RegistrationStatus.Approved;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string FullName => $"{FirstName} {LastName}".Trim();
}

/// <summary>Platform-wide key/value configuration (not tenant-scoped). Secrets never go here.</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

/// <summary>One-time code sent to verify a self-registration's email or mobile; only the hash is stored.</summary>
public class VerificationCode
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    public VerificationChannel Channel { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? ConsumedAt { get; set; }
}

/// <summary>Single-use link token for "forgot password"; only the hash is stored.</summary>
public class PasswordResetToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public abstract class AuditableEntity : ITenantEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public abstract class OwnedEntity : AuditableEntity, IHasCustomFields
{
    public Guid OwnerId { get; set; }
    public AppUser? Owner { get; set; }
    public string CustomFields { get; set; } = "{}";
}

public class Account : OwnedEntity
{
    public string Name { get; set; } = "";
    public string? Industry { get; set; }
    public string? Website { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public decimal? AnnualRevenue { get; set; }
    public int? EmployeeCount { get; set; }
    public string? Description { get; set; }
    public List<Contact> Contacts { get; set; } = [];
    public List<Opportunity> Opportunities { get; set; } = [];
}

public class Contact : OwnedEntity
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Mobile { get; set; }
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? Description { get; set; }
    public Guid? AccountId { get; set; }
    public Account? Account { get; set; }
}

public class Lead : OwnedEntity
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Company { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? JobTitle { get; set; }
    public string Source { get; set; } = "Other";
    public LeadStatus Status { get; set; } = LeadStatus.New;
    public decimal? EstimatedValue { get; set; }
    public string? Description { get; set; }
    public DateTime? ConvertedAt { get; set; }
    public Guid? ConvertedAccountId { get; set; }
    public Account? ConvertedAccount { get; set; }
    public Guid? ConvertedContactId { get; set; }
    public Contact? ConvertedContact { get; set; }
    public Guid? ConvertedOpportunityId { get; set; }
    public Opportunity? ConvertedOpportunity { get; set; }
}

public interface IStaged
{
    string Stage { get; set; }
    DateTime? ClosedAt { get; set; }
}

public class Opportunity : OwnedEntity, IStaged
{
    public string Name { get; set; } = "";
    public Guid AccountId { get; set; }
    public Account? Account { get; set; }
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }
    public decimal Amount { get; set; }
    public string Stage { get; set; } = "";
    public int Probability { get; set; }
    public DateOnly ExpectedCloseDate { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? LossReason { get; set; }
    public string? Description { get; set; }

    public void MoveToStage(PipelineStage stage, DateTime now, int? probability = null)
    {
        if (stage.Key != Stage)
        {
            Stage = stage.Key;
            ClosedAt = stage.Category == StageCategory.Open ? null : now;
            Probability = stage.Probability;
        }
        if (probability is not null && stage.Category == StageCategory.Open) Probability = probability.Value;
        if (stage.Category != StageCategory.Lost) LossReason = null;
    }
}

public class Activity : OwnedEntity
{
    public ActivityType Type { get; set; } = ActivityType.Task;
    public string Subject { get; set; } = "";
    public string? Description { get; set; }
    public DateTime? DueAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public ActivityPriority Priority { get; set; } = ActivityPriority.Normal;
    public Guid? AccountId { get; set; }
    public Account? Account { get; set; }
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }
    public Guid? LeadId { get; set; }
    public Lead? Lead { get; set; }
    public Guid? OpportunityId { get; set; }
    public Opportunity? Opportunity { get; set; }
    public Guid? CustomRecordId { get; set; }
    public CustomRecord? CustomRecord { get; set; }
}

/// <summary>A row of a tenant-defined object type; its fields live in <see cref="OwnedEntity.CustomFields"/>.</summary>
public class CustomRecord : OwnedEntity, IStaged
{
    public Guid ObjectDefinitionId { get; set; }
    public ObjectDefinition? ObjectDefinition { get; set; }
    public string Name { get; set; } = "";
    public string Stage { get; set; } = "";
    public DateTime? ClosedAt { get; set; }
}

// ---------- Metadata (configuration) ----------

public static class ObjectKeys
{
    public const string Account = "account";
    public const string Contact = "contact";
    public const string Lead = "lead";
    public const string Opportunity = "opportunity";
    public const string Activity = "activity";
    public static readonly string[] Standard = [Account, Contact, Lead, Opportunity, Activity];
}

public class ObjectDefinition : AuditableEntity
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string PluralLabel { get; set; } = "";
    public string Icon { get; set; } = "category";
    public string? Description { get; set; }
    public string NameLabel { get; set; } = "Name";
    public bool IsStandard { get; set; }
    public bool IsActive { get; set; } = true;
    public bool HasPipeline { get; set; }
    public int SortOrder { get; set; }
}

public class FieldDefinition : AuditableEntity
{
    public string ObjectKey { get; set; } = "";
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public FieldType Type { get; set; }
    public bool IsRequired { get; set; }
    /// <summary>No two records of the object may share a value (emails compared case-insensitively).</summary>
    public bool IsUnique { get; set; }
    /// <summary>JSON array of allowed values for Picklist / MultiPicklist.</summary>
    public string Options { get; set; } = "[]";
    public string? LookupObjectKey { get; set; }
    public int? MaxLength { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public string? HelpText { get; set; }
    public bool ShowInList { get; set; }
    public int SortOrder { get; set; }
}

public class PipelineStage : AuditableEntity
{
    public string ObjectKey { get; set; } = "";
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public int SortOrder { get; set; }
    public int Probability { get; set; }
    public StageCategory Category { get; set; }
    public string Color { get; set; } = "#6750a4";
}

/// <summary>Configurable option list for a standard field, e.g. "account.industry" or "lead.source".</summary>
public class Picklist : AuditableEntity
{
    public string Key { get; set; } = "";
    public string Options { get; set; } = "[]";
}

public class AutomationRule : AuditableEntity
{
    public string Name { get; set; } = "";
    public string ObjectKey { get; set; } = "";
    public AutomationTrigger Trigger { get; set; }
    public string Conditions { get; set; } = "[]";
    public string Actions { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
    public int RunCount { get; set; }
    public DateTime? LastRunAt { get; set; }
    /// <summary>DateReached only: the date field the schedule is relative to, e.g. <c>cf.interview_date</c> or <c>updatedAt</c>.</summary>
    public string? ScheduleField { get; set; }
    /// <summary>DateReached only: negative runs before the date, positive after.</summary>
    public int ScheduleOffsetMinutes { get; set; }
}

public record AutomationCondition(string Field, ConditionOperator Operator, string? Value);

public record AutomationAction(
    AutomationActionType Type,
    string? Subject = null,
    string? Message = null,
    int? DueInDays = null,
    ActivityPriority? Priority = null,
    string? AssignTo = null,
    string? Field = null,
    string? Value = null,
    string? To = null,
    Guid? TemplateId = null);

/// <summary>Reusable email for one object; placeholders are filled from the record when it is sent.</summary>
public class EmailTemplate : AuditableEntity
{
    public string Name { get; set; } = "";
    public string ObjectKey { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
}

/// <summary>Transactional outbox row: saved together with the change that caused it, then delivered by the dispatcher.</summary>
public class OutboundEmail : ITenantEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public string To { get; set; } = "";
    public string? ReplyTo { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string? ObjectKey { get; set; }
    public Guid? RecordId { get; set; }
    public string? RecordName { get; set; }
    public Guid? RuleId { get; set; }
    public Guid? TemplateId { get; set; }
    /// <summary>The Email activity logged on the record's timeline.</summary>
    public Guid? ActivityId { get; set; }
    public Guid? SentById { get; set; }
    public string SentByName { get; set; } = "";
    public OutboundEmailStatus Status { get; set; } = OutboundEmailStatus.Pending;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    /// <summary>Lease taken by the dispatcher instance currently sending it.</summary>
    public DateTime? LockedUntil { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}

/// <summary>Marks a date-based rule as handled for one record and due time, so each due date fires once, even across API instances.</summary>
public class ScheduledRuleRun : ITenantEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid RuleId { get; set; }
    public Guid RecordId { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime RanAt { get; set; }
    /// <summary>False when the rule's conditions didn't match at the due time.</summary>
    public bool Matched { get; set; }
}

public class Notification : ITenantEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Link { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

/// <summary>A file attached to a record; the bytes live in <c>IFileStorage</c> under <see cref="StorageKey"/>.</summary>
public class Attachment : ITenantEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public string ObjectKey { get; set; } = "";
    public Guid RecordId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public string StorageKey { get; set; } = "";
    public Guid UploadedById { get; set; }
    public string UploadedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class AuditLog : ITenantEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string? EntityName { get; set; }
    public AuditAction Action { get; set; }
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public DateTime Timestamp { get; set; }
    /// <summary>JSON object: field name -> [old, new].</summary>
    public string Changes { get; set; } = "{}";
}

// ---------- Security ----------

public static class AdminResources
{
    public const string Users = "users";
    public const string Roles = "roles";
    public const string Groups = "groups";
    public const string Customization = "customization";
    public const string Settings = "settings";
    public const string Audit = "audit";
    public static readonly string[] All = [Users, Roles, Groups, Customization, Settings, Audit];
}

public static class PermissionActions
{
    public const string Read = "read";
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
    public const string Manage = "manage";
    public static readonly string[] Record = [Read, Create, Update, Delete];
}

public class Role : AuditableEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>System roles are created by templates and cannot be deleted.</summary>
    public bool IsSystem { get; set; }
    /// <summary>Grants every permission with All scope; cannot be edited.</summary>
    public bool IsAdministrator { get; set; }
    public List<RolePermission> Permissions { get; set; } = [];
}

/// <summary>Resource is an object key, "*" for every object, or an admin resource.</summary>
public class RolePermission
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RoleId { get; set; }
    public string Resource { get; set; } = "";
    public string Action { get; set; } = "";
    public PermissionScope Scope { get; set; } = PermissionScope.All;
}

public class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
}

public class Group : AuditableEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<GroupMember> Members { get; set; } = [];
    public List<GroupRole> Roles { get; set; } = [];
}

public class GroupMember
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
}

public class GroupRole
{
    public Guid GroupId { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
}
