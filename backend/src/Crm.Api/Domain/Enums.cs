namespace Crm.Api.Domain;

public enum LeadStatus { New, Contacted, Qualified, Unqualified, Converted }

public enum ActivityType { Task, Call, Meeting, Email, Note }

public enum ActivityPriority { Low, Normal, High }

public enum TenantStatus { Active, Suspended }

/// <summary>Which records a grant covers: records the user owns, records owned by members of the user's groups, or all.</summary>
public enum PermissionScope { None, Own, Group, All }

public enum FieldType { Text, TextArea, Number, Currency, Date, DateTime, Boolean, Picklist, MultiPicklist, Email, Phone, Url, Lookup }

/// <summary>Won = closed successfully (deal won, ticket resolved, candidate hired); Lost = closed unsuccessfully.</summary>
public enum StageCategory { Open, Won, Lost }

/// <summary>DateReached rules run on a schedule, relative to a date field, rather than when a record is saved.</summary>
public enum AutomationTrigger { Created, Updated, StageChanged, DateReached }

public enum ConditionOperator { Equals, NotEquals, GreaterThan, LessThan, Contains, IsEmpty, IsNotEmpty, Changed }

public enum AutomationActionType { CreateTask, Notify, SetField, SendEmail }

public enum OutboundEmailStatus { Pending, Sent, Failed, Cancelled }

public enum InterviewKind { Phone, Video, Onsite }

public enum InterviewStatus { Scheduled, Completed, Cancelled }

public enum Recommendation { StrongNo, No, Yes, StrongYes }

/// <summary>Draft and Sent are active (one per application); the others are final.</summary>
public enum OfferStatus { Draft, Sent, Accepted, Declined, Withdrawn, Expired }

public enum PayPeriod { Year, Month, Hour }

/// <summary>Erased: a person's data was removed for privacy (request or retention); the entry itself holds no personal data.</summary>
public enum AuditAction { Created, Updated, Deleted, Exported, Erased }

/// <summary>Only Approved users may sign in; the other states belong to self-registrations still in progress.</summary>
public enum RegistrationStatus { Approved, PendingVerification, PendingApproval, Rejected }

public enum VerificationChannel { Email, Sms }
