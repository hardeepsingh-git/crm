using Crm.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Api.Data;

internal static class ConfigExtensions
{
    /// <summary>JSON documents: jsonb on PostgreSQL (the migration provider), plain text elsewhere.</summary>
    public static PropertyBuilder<string> IsJson(this PropertyBuilder<string> p, string defaultJson = "{}") =>
        p.HasColumnType("jsonb").IsRequired().HasDefaultValueSql($"'{defaultJson}'");

    public static void OwnedBy<T>(this EntityTypeBuilder<T> b) where T : OwnedEntity
    {
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.CustomFields).IsJson();
        b.HasIndex(x => new { x.TenantId, x.OwnerId });
    }
}

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Slug).HasMaxLength(64).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Template).HasMaxLength(32).IsRequired();
        b.Property(x => x.CareersIntro).HasMaxLength(2000);
        b.HasIndex(x => x.Slug).IsUnique();
    }
}

public class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> b)
    {
        b.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
        b.Property(x => x.LastName).HasMaxLength(100).IsRequired();
        b.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.TenantId);
        b.HasIndex(x => new { x.TenantId, x.RegistrationStatus });
    }
}

public class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    public void Configure(EntityTypeBuilder<AppSetting> b)
    {
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(100);
        b.Property(x => x.Value).HasMaxLength(2000).IsRequired();
    }
}

public class VerificationCodeConfiguration : IEntityTypeConfiguration<VerificationCode>
{
    public void Configure(EntityTypeBuilder<VerificationCode> b)
    {
        b.Property(x => x.CodeHash).HasMaxLength(128).IsRequired();
        b.HasIndex(x => new { x.UserId, x.Channel });
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> b)
    {
        b.Property(x => x.ObjectKey).HasMaxLength(40).IsRequired();
        b.Property(x => x.FileName).HasMaxLength(255).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Property(x => x.StorageKey).HasMaxLength(200).IsRequired();
        b.Property(x => x.UploadedByName).HasMaxLength(200).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.ObjectKey, x.RecordId });
    }
}

public class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> b)
    {
        b.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.UserId);
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> b)
    {
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Industry).HasMaxLength(100);
        b.Property(x => x.Website).HasMaxLength(300);
        b.Property(x => x.Phone).HasMaxLength(50);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Street).HasMaxLength(200);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.State).HasMaxLength(100);
        b.Property(x => x.PostalCode).HasMaxLength(20);
        b.Property(x => x.Country).HasMaxLength(100);
        b.Property(x => x.Description).HasMaxLength(4000);
        b.HasIndex(x => new { x.TenantId, x.Name });
        b.OwnedBy();
    }
}

public class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> b)
    {
        b.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
        b.Property(x => x.LastName).HasMaxLength(100).IsRequired();
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Phone).HasMaxLength(50);
        b.Property(x => x.Mobile).HasMaxLength(50);
        b.Property(x => x.JobTitle).HasMaxLength(100);
        b.Property(x => x.Department).HasMaxLength(100);
        b.Property(x => x.Description).HasMaxLength(4000);
        b.HasIndex(x => new { x.TenantId, x.LastName });
        b.HasIndex(x => x.Email);
        b.HasOne(x => x.Account).WithMany(a => a.Contacts).HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
        b.OwnedBy();
    }
}

public class LeadConfiguration : IEntityTypeConfiguration<Lead>
{
    public void Configure(EntityTypeBuilder<Lead> b)
    {
        b.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
        b.Property(x => x.LastName).HasMaxLength(100).IsRequired();
        b.Property(x => x.Company).HasMaxLength(200).IsRequired();
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Phone).HasMaxLength(50);
        b.Property(x => x.JobTitle).HasMaxLength(100);
        b.Property(x => x.Source).HasMaxLength(64).IsRequired();
        b.Property(x => x.Description).HasMaxLength(4000);
        b.HasIndex(x => new { x.TenantId, x.Status });
        b.HasIndex(x => x.Email);
        b.HasOne(x => x.ConvertedAccount).WithMany().HasForeignKey(x => x.ConvertedAccountId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.ConvertedContact).WithMany().HasForeignKey(x => x.ConvertedContactId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.ConvertedOpportunity).WithMany().HasForeignKey(x => x.ConvertedOpportunityId).OnDelete(DeleteBehavior.SetNull);
        b.OwnedBy();
    }
}

public class OpportunityConfiguration : IEntityTypeConfiguration<Opportunity>
{
    public void Configure(EntityTypeBuilder<Opportunity> b)
    {
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Stage).HasMaxLength(64).IsRequired();
        b.Property(x => x.LossReason).HasMaxLength(500);
        b.Property(x => x.Description).HasMaxLength(4000);
        b.HasIndex(x => new { x.TenantId, x.Stage });
        b.HasIndex(x => x.ExpectedCloseDate);
        b.HasOne(x => x.Account).WithMany(a => a.Opportunities).HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Contact).WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
        b.OwnedBy();
    }
}

public class ActivityConfiguration : IEntityTypeConfiguration<Activity>
{
    public void Configure(EntityTypeBuilder<Activity> b)
    {
        b.Property(x => x.Subject).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(4000);
        b.HasIndex(x => new { x.TenantId, x.DueAt });
        b.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Contact).WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Opportunity).WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.CustomRecord).WithMany().HasForeignKey(x => x.CustomRecordId).OnDelete(DeleteBehavior.Cascade);
        b.OwnedBy();
    }
}

public class CustomRecordConfiguration : IEntityTypeConfiguration<CustomRecord>
{
    public void Configure(EntityTypeBuilder<CustomRecord> b)
    {
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Stage).HasMaxLength(64).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.ObjectDefinitionId, x.Stage });
        b.HasIndex(x => new { x.TenantId, x.ObjectDefinitionId, x.Name });
        b.HasOne(x => x.ObjectDefinition).WithMany().HasForeignKey(x => x.ObjectDefinitionId).OnDelete(DeleteBehavior.Restrict);
        b.OwnedBy();
    }
}

public class ObjectDefinitionConfiguration : IEntityTypeConfiguration<ObjectDefinition>
{
    public void Configure(EntityTypeBuilder<ObjectDefinition> b)
    {
        b.Property(x => x.Key).HasMaxLength(40).IsRequired();
        b.Property(x => x.Label).HasMaxLength(100).IsRequired();
        b.Property(x => x.PluralLabel).HasMaxLength(100).IsRequired();
        b.Property(x => x.Icon).HasMaxLength(64).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.NameLabel).HasMaxLength(100).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
    }
}

public class FieldDefinitionConfiguration : IEntityTypeConfiguration<FieldDefinition>
{
    public void Configure(EntityTypeBuilder<FieldDefinition> b)
    {
        b.Property(x => x.ObjectKey).HasMaxLength(40).IsRequired();
        b.Property(x => x.Key).HasMaxLength(40).IsRequired();
        b.Property(x => x.Label).HasMaxLength(100).IsRequired();
        b.Property(x => x.Options).IsJson("[]");
        b.Property(x => x.LookupObjectKey).HasMaxLength(40);
        b.Property(x => x.HelpText).HasMaxLength(500);
        b.HasIndex(x => new { x.TenantId, x.ObjectKey, x.Key }).IsUnique();
    }
}

public class PipelineStageConfiguration : IEntityTypeConfiguration<PipelineStage>
{
    public void Configure(EntityTypeBuilder<PipelineStage> b)
    {
        b.Property(x => x.ObjectKey).HasMaxLength(40).IsRequired();
        b.Property(x => x.Key).HasMaxLength(64).IsRequired();
        b.Property(x => x.Label).HasMaxLength(100).IsRequired();
        b.Property(x => x.Color).HasMaxLength(16).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.ObjectKey, x.Key }).IsUnique();
    }
}

public class PicklistConfiguration : IEntityTypeConfiguration<Picklist>
{
    public void Configure(EntityTypeBuilder<Picklist> b)
    {
        b.Property(x => x.Key).HasMaxLength(64).IsRequired();
        b.Property(x => x.Options).IsJson("[]");
        b.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
    }
}

public class AutomationRuleConfiguration : IEntityTypeConfiguration<AutomationRule>
{
    public void Configure(EntityTypeBuilder<AutomationRule> b)
    {
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.ObjectKey).HasMaxLength(40).IsRequired();
        b.Property(x => x.Conditions).IsJson("[]");
        b.Property(x => x.Actions).IsJson("[]");
        b.Property(x => x.ScheduleField).HasMaxLength(80);
        b.HasIndex(x => new { x.TenantId, x.ObjectKey });
        b.HasIndex(x => new { x.Trigger, x.IsActive });
    }
}

public class EmailTemplateConfiguration : IEntityTypeConfiguration<EmailTemplate>
{
    public void Configure(EntityTypeBuilder<EmailTemplate> b)
    {
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.ObjectKey).HasMaxLength(40).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(300).IsRequired();
        b.Property(x => x.Body).HasMaxLength(10000).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
    }
}

public class OutboundEmailConfiguration : IEntityTypeConfiguration<OutboundEmail>
{
    public void Configure(EntityTypeBuilder<OutboundEmail> b)
    {
        b.Property(x => x.To).HasMaxLength(320).IsRequired();
        b.Property(x => x.ReplyTo).HasMaxLength(320);
        b.Property(x => x.Subject).HasMaxLength(300).IsRequired();
        b.Property(x => x.Body).HasMaxLength(10000).IsRequired();
        b.Property(x => x.ObjectKey).HasMaxLength(40);
        b.Property(x => x.RecordName).HasMaxLength(300);
        b.Property(x => x.SentByName).HasMaxLength(200).IsRequired();
        b.Property(x => x.LastError).HasMaxLength(1000);
        // The dispatcher scans across tenants for due mail.
        b.HasIndex(x => new { x.Status, x.NextAttemptAt });
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.HasIndex(x => new { x.TenantId, x.RuleId, x.RecordId });
    }
}

public class ScheduledRuleRunConfiguration : IEntityTypeConfiguration<ScheduledRuleRun>
{
    public void Configure(EntityTypeBuilder<ScheduledRuleRun> b)
    {
        b.HasIndex(x => new { x.RuleId, x.RecordId, x.DueAt }).IsUnique();
        b.HasIndex(x => x.TenantId);
    }
}

public class InterviewConfiguration : IEntityTypeConfiguration<Interview>
{
    public void Configure(EntityTypeBuilder<Interview> b)
    {
        b.Property(x => x.Location).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.HasOne(x => x.Application).WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Participants).WithOne().HasForeignKey(p => p.InterviewId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Scorecards).WithOne().HasForeignKey(s => s.InterviewId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.TenantId, x.ApplicationId });
        b.HasIndex(x => new { x.TenantId, x.ScheduledAt });
    }
}

public class InterviewParticipantConfiguration : IEntityTypeConfiguration<InterviewParticipant>
{
    public void Configure(EntityTypeBuilder<InterviewParticipant> b)
    {
        b.HasKey(x => new { x.InterviewId, x.UserId });
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.UserId);
    }
}

public class ScorecardConfiguration : IEntityTypeConfiguration<Scorecard>
{
    public void Configure(EntityTypeBuilder<Scorecard> b)
    {
        b.Property(x => x.Ratings).IsJson();
        b.Property(x => x.Notes).HasMaxLength(4000);
        b.HasIndex(x => new { x.InterviewId, x.InterviewerId }).IsUnique();
    }
}

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Message).HasMaxLength(1000).IsRequired();
        b.Property(x => x.Link).HasMaxLength(300);
        b.HasIndex(x => new { x.TenantId, x.UserId, x.ReadAt });
    }
}

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.Property(x => x.EntityType).HasMaxLength(64).IsRequired();
        b.Property(x => x.EntityName).HasMaxLength(300);
        b.Property(x => x.UserName).HasMaxLength(200);
        b.Property(x => x.Changes).IsJson();
        b.HasIndex(x => new { x.TenantId, x.EntityId });
        b.HasIndex(x => new { x.TenantId, x.Timestamp });
    }
}

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("Roles");
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        b.HasMany(x => x.Permissions).WithOne().HasForeignKey(p => p.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.Property(x => x.Resource).HasMaxLength(40).IsRequired();
        b.Property(x => x.Action).HasMaxLength(16).IsRequired();
        b.HasIndex(x => new { x.RoleId, x.Resource, x.Action }).IsUnique();
    }
}

public class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> b)
    {
        b.ToTable("UserRoles");
        b.HasKey(x => new { x.UserId, x.RoleId });
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class GroupConfiguration : IEntityTypeConfiguration<Group>
{
    public void Configure(EntityTypeBuilder<Group> b)
    {
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        b.HasMany(x => x.Members).WithOne().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Roles).WithOne().HasForeignKey(r => r.GroupId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class GroupMemberConfiguration : IEntityTypeConfiguration<GroupMember>
{
    public void Configure(EntityTypeBuilder<GroupMember> b)
    {
        b.HasKey(x => new { x.GroupId, x.UserId });
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class GroupRoleConfiguration : IEntityTypeConfiguration<GroupRole>
{
    public void Configure(EntityTypeBuilder<GroupRole> b)
    {
        b.HasKey(x => new { x.GroupId, x.RoleId });
        b.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}
