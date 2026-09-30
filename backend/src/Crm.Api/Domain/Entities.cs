using Microsoft.AspNetCore.Identity;

namespace Crm.Api.Domain;

public class AppUser : IdentityUser<Guid>
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string FullName => $"{FirstName} {LastName}".Trim();
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

public abstract class AuditableEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public abstract class OwnedEntity : AuditableEntity
{
    public Guid OwnerId { get; set; }
    public AppUser? Owner { get; set; }
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
    public LeadSource Source { get; set; } = LeadSource.Other;
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

public class Opportunity : OwnedEntity
{
    public string Name { get; set; } = "";
    public Guid AccountId { get; set; }
    public Account? Account { get; set; }
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }
    public decimal Amount { get; set; }
    public OpportunityStage Stage { get; set; } = OpportunityStage.Prospecting;
    public int Probability { get; set; } = 10;
    public DateOnly ExpectedCloseDate { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? LossReason { get; set; }
    public string? Description { get; set; }

    public bool IsClosed => Stage is OpportunityStage.ClosedWon or OpportunityStage.ClosedLost;

    public void MoveToStage(OpportunityStage stage, DateTime now, int? probability = null)
    {
        if (stage != Stage)
        {
            Stage = stage;
            ClosedAt = IsClosed ? now : null;
            Probability = StageDefaults.Probability(stage);
        }
        if (probability is not null && !IsClosed) Probability = probability.Value;
        if (stage != OpportunityStage.ClosedLost) LossReason = null;
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
}
