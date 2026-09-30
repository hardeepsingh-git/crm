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

public enum AutomationTrigger { Created, Updated, StageChanged }

public enum ConditionOperator { Equals, NotEquals, GreaterThan, LessThan, Contains, IsEmpty, IsNotEmpty, Changed }

public enum AutomationActionType { CreateTask, Notify, SetField }

public enum AuditAction { Created, Updated, Deleted }

/// <summary>Only Approved users may sign in; the other states belong to self-registrations still in progress.</summary>
public enum RegistrationStatus { Approved, PendingVerification, PendingApproval, Rejected }

public enum VerificationChannel { Email, Sms }
