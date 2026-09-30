namespace Crm.Api.Domain;

public enum LeadSource { Web, Referral, Event, ColdCall, Advertisement, Partner, Other }

public enum LeadStatus { New, Contacted, Qualified, Unqualified, Converted }

public enum OpportunityStage { Prospecting, Qualification, Proposal, Negotiation, ClosedWon, ClosedLost }

public enum ActivityType { Task, Call, Meeting, Email, Note }

public enum ActivityPriority { Low, Normal, High }

public static class StageDefaults
{
    public static int Probability(OpportunityStage stage) => stage switch
    {
        OpportunityStage.Prospecting => 10,
        OpportunityStage.Qualification => 25,
        OpportunityStage.Proposal => 50,
        OpportunityStage.Negotiation => 75,
        OpportunityStage.ClosedWon => 100,
        _ => 0,
    };
}
