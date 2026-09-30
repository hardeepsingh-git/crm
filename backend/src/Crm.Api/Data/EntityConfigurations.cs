using Crm.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Api.Data;

public class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> b)
    {
        b.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
        b.Property(x => x.LastName).HasMaxLength(100).IsRequired();
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
        b.HasIndex(x => x.Name);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
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
        b.HasIndex(x => x.LastName);
        b.HasIndex(x => x.Email);
        b.HasOne(x => x.Account).WithMany(a => a.Contacts).HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
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
        b.Property(x => x.Description).HasMaxLength(4000);
        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.Email);
        b.HasOne(x => x.ConvertedAccount).WithMany().HasForeignKey(x => x.ConvertedAccountId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.ConvertedContact).WithMany().HasForeignKey(x => x.ConvertedContactId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.ConvertedOpportunity).WithMany().HasForeignKey(x => x.ConvertedOpportunityId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class OpportunityConfiguration : IEntityTypeConfiguration<Opportunity>
{
    public void Configure(EntityTypeBuilder<Opportunity> b)
    {
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.LossReason).HasMaxLength(500);
        b.Property(x => x.Description).HasMaxLength(4000);
        b.HasIndex(x => x.Stage);
        b.HasIndex(x => x.ExpectedCloseDate);
        b.HasOne(x => x.Account).WithMany(a => a.Opportunities).HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Contact).WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ActivityConfiguration : IEntityTypeConfiguration<Activity>
{
    public void Configure(EntityTypeBuilder<Activity> b)
    {
        b.Property(x => x.Subject).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(4000);
        b.HasIndex(x => x.DueAt);
        b.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Contact).WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Opportunity).WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
    }
}
