export type LeadStatus = 'New' | 'Contacted' | 'Qualified' | 'Unqualified' | 'Converted';
export type ActivityType = 'Task' | 'Call' | 'Meeting' | 'Email' | 'Note';
export type ActivityPriority = 'Low' | 'Normal' | 'High';
export type PermissionScope = 'None' | 'Own' | 'Group' | 'All';
export type StageCategory = 'Open' | 'Won' | 'Lost';
export type FieldType =
  | 'Text' | 'TextArea' | 'Number' | 'Currency' | 'Date' | 'DateTime' | 'Boolean'
  | 'Picklist' | 'MultiPicklist' | 'Email' | 'Phone' | 'Url' | 'Lookup';
export type AutomationTrigger = 'Created' | 'Updated' | 'StageChanged';
export type ConditionOperator = 'Equals' | 'NotEquals' | 'GreaterThan' | 'LessThan' | 'Contains' | 'IsEmpty' | 'IsNotEmpty' | 'Changed';
export type AutomationActionType = 'CreateTask' | 'Notify' | 'SetField';
export type TenantStatus = 'Active' | 'Suspended';
export type AuditAction = 'Created' | 'Updated' | 'Deleted';
export type RecordAction = 'read' | 'create' | 'update' | 'delete';
export type AdminResource = 'users' | 'roles' | 'groups' | 'customization' | 'settings' | 'audit';

export const LEAD_STATUSES: LeadStatus[] = ['New', 'Contacted', 'Qualified', 'Unqualified', 'Converted'];
export const ACTIVITY_TYPES: ActivityType[] = ['Task', 'Call', 'Meeting', 'Email', 'Note'];
export const PRIORITIES: ActivityPriority[] = ['Low', 'Normal', 'High'];
export const FIELD_TYPES: FieldType[] = [
  'Text', 'TextArea', 'Number', 'Currency', 'Date', 'DateTime', 'Boolean', 'Picklist', 'MultiPicklist', 'Email', 'Phone', 'Url', 'Lookup',
];
export const SCOPES: PermissionScope[] = ['None', 'Own', 'Group', 'All'];
export const RECORD_ACTIONS: RecordAction[] = ['read', 'create', 'update', 'delete'];
export const ADMIN_RESOURCES: { key: AdminResource; label: string; action: 'manage' | 'read' }[] = [
  { key: 'users', label: 'Manage users', action: 'manage' },
  { key: 'groups', label: 'Manage groups', action: 'manage' },
  { key: 'roles', label: 'Manage roles & permissions', action: 'manage' },
  { key: 'customization', label: 'Customize objects, fields & automation', action: 'manage' },
  { key: 'settings', label: 'Organisation settings', action: 'manage' },
  { key: 'audit', label: 'View audit log', action: 'read' },
];

export type CustomFields = Record<string, unknown>;

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface TenantInfo {
  id: string;
  name: string;
  slug: string;
  currency: string;
  template: string;
}

export interface UserInfo {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  fullName: string;
  roles: string[];
  tenant: TenantInfo;
  isAdministrator: boolean;
  isPlatformAdmin: boolean;
  permissions: Record<string, Record<string, PermissionScope>>;
  teamUserIds: string[];
}

export interface AuthResponse {
  accessToken: string;
  expiresAt: string;
  refreshToken: string;
  user: UserInfo;
}

// ---------- Metadata ----------

export interface FieldDef {
  id: string;
  key: string;
  label: string;
  type: FieldType;
  isRequired: boolean;
  options: string[];
  lookupObjectKey: string | null;
  maxLength: number | null;
  min: number | null;
  max: number | null;
  helpText: string | null;
  showInList: boolean;
  sortOrder: number;
}

export interface StageDef {
  key: string;
  label: string;
  probability: number;
  category: StageCategory;
  color: string;
}

export interface ObjectDef {
  key: string;
  label: string;
  pluralLabel: string;
  icon: string;
  description: string | null;
  nameLabel: string;
  isStandard: boolean;
  isActive: boolean;
  hasPipeline: boolean;
  sortOrder: number;
  fields: FieldDef[];
  stages: StageDef[];
}

export interface Metadata {
  tenant: TenantInfo;
  objects: ObjectDef[];
  picklists: Record<string, string[]>;
}

// ---------- Records ----------

interface Owned {
  id: string;
  ownerId: string;
  ownerName: string;
  createdAt: string;
  customFields: CustomFields;
}

export interface AccountListItem extends Owned {
  name: string;
  industry: string | null;
  phone: string | null;
  email: string | null;
  city: string | null;
  country: string | null;
  contactCount: number;
  openOpportunityCount: number;
}

export interface AccountDetail extends AccountListItem {
  website: string | null;
  street: string | null;
  state: string | null;
  postalCode: string | null;
  annualRevenue: number | null;
  employeeCount: number | null;
  description: string | null;
  openPipelineAmount: number;
  updatedAt: string;
}

export type AccountRequest = Omit<AccountDetail,
  'id' | 'ownerName' | 'createdAt' | 'updatedAt' | 'contactCount' | 'openOpportunityCount' | 'openPipelineAmount' | 'ownerId'> & { ownerId: string | null };

export interface ContactListItem extends Owned {
  firstName: string;
  lastName: string;
  fullName: string;
  email: string | null;
  phone: string | null;
  jobTitle: string | null;
  accountId: string | null;
  accountName: string | null;
}

export interface ContactDetail extends ContactListItem {
  mobile: string | null;
  department: string | null;
  description: string | null;
  updatedAt: string;
}

export interface ContactRequest {
  firstName: string;
  lastName: string;
  email: string | null;
  phone: string | null;
  mobile: string | null;
  jobTitle: string | null;
  department: string | null;
  description: string | null;
  accountId: string | null;
  ownerId: string | null;
  customFields: CustomFields;
}

export interface LeadListItem extends Owned {
  firstName: string;
  lastName: string;
  fullName: string;
  company: string;
  email: string | null;
  phone: string | null;
  status: LeadStatus;
  source: string;
  estimatedValue: number | null;
}

export interface LeadDetail extends LeadListItem {
  jobTitle: string | null;
  description: string | null;
  convertedAt: string | null;
  convertedAccountId: string | null;
  convertedContactId: string | null;
  convertedOpportunityId: string | null;
  updatedAt: string;
}

export interface LeadRequest {
  firstName: string;
  lastName: string;
  company: string;
  email: string | null;
  phone: string | null;
  jobTitle: string | null;
  source: string;
  status: LeadStatus;
  estimatedValue: number | null;
  description: string | null;
  ownerId: string | null;
  customFields: CustomFields;
}

export interface LeadConvertRequest {
  existingAccountId: string | null;
  createOpportunity: boolean;
  opportunityName: string | null;
  amount: number | null;
  expectedCloseDate: string | null;
}

export interface LeadConvertResult {
  accountId: string;
  contactId: string;
  opportunityId: string | null;
}

export interface OpportunityListItem extends Owned {
  name: string;
  accountId: string;
  accountName: string;
  contactId: string | null;
  contactName: string | null;
  amount: number;
  stage: string;
  probability: number;
  expectedCloseDate: string;
}

export interface OpportunityDetail extends OpportunityListItem {
  closedAt: string | null;
  lossReason: string | null;
  description: string | null;
  updatedAt: string;
}

export interface OpportunityRequest {
  name: string;
  accountId: string;
  contactId: string | null;
  amount: number;
  stage: string;
  probability: number | null;
  expectedCloseDate: string;
  lossReason: string | null;
  description: string | null;
  ownerId: string | null;
  customFields: CustomFields;
}

export interface ActivityItem extends Owned {
  type: ActivityType;
  subject: string;
  description: string | null;
  dueAt: string | null;
  completedAt: string | null;
  priority: ActivityPriority;
  accountId: string | null;
  accountName: string | null;
  contactId: string | null;
  contactName: string | null;
  leadId: string | null;
  leadName: string | null;
  opportunityId: string | null;
  opportunityName: string | null;
  customRecordId: string | null;
  customRecordName: string | null;
  customObjectKey: string | null;
  updatedAt: string;
}

export interface ActivityRequest {
  type: ActivityType;
  subject: string;
  description: string | null;
  dueAt: string | null;
  priority: ActivityPriority;
  accountId: string | null;
  contactId: string | null;
  leadId: string | null;
  opportunityId: string | null;
  customRecordId: string | null;
  ownerId: string | null;
  customFields?: CustomFields;
}

export interface ActivityLinks {
  accountId?: string | null;
  contactId?: string | null;
  leadId?: string | null;
  opportunityId?: string | null;
  customRecordId?: string | null;
}

export interface RecordItem {
  id: string;
  objectKey: string;
  name: string;
  stage: string;
  closedAt: string | null;
  ownerId: string;
  ownerName: string;
  createdAt: string;
  updatedAt: string;
  customFields: CustomFields;
}

export interface RecordRequest {
  name: string;
  stage: string | null;
  ownerId: string | null;
  customFields: CustomFields;
}

export interface StageSummary {
  stage: string;
  label: string;
  color: string;
  count: number;
  amount: number;
}

export interface DashboardSummary {
  sections: string[];
  accountCount: number;
  contactCount: number;
  openLeadCount: number;
  openOpportunityCount: number;
  openPipelineAmount: number;
  weightedPipelineAmount: number;
  wonThisMonthCount: number;
  wonThisMonthAmount: number;
  winRate: number;
  overdueActivityCount: number;
  pipelineByStage: StageSummary[];
  leadsBySource: { source: string; count: number }[];
  wonByMonth: { year: number; month: number; count: number; amount: number }[];
  upcomingActivities: ActivityItem[];
  customPipelines: { objectKey: string; label: string; icon: string; openCount: number; stages: StageSummary[] }[];
}

// ---------- Security & administration ----------

export interface NamedRef {
  id: string;
  name: string;
}

export interface UserLookup {
  id: string;
  fullName: string;
  email: string;
}

export interface UserItem {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  fullName: string;
  isActive: boolean;
  isPlatformAdmin: boolean;
  roles: NamedRef[];
  groups: NamedRef[];
  createdAt: string;
  lockoutEnd: string | null;
  registrationStatus: RegistrationStatus;
  phoneNumber: string | null;
  emailConfirmed: boolean;
  phoneNumberConfirmed: boolean;
}

// ---------- Self-registration ----------

export type RegistrationStatus = 'Approved' | 'PendingVerification' | 'PendingApproval' | 'Rejected';
export type VerificationChannel = 'Email' | 'Sms';

export interface RegistrationOptions {
  available: boolean;
  tenantName: string | null;
  emailVerification: boolean;
  smsVerification: boolean;
}

export interface RegisterRequest {
  tenantCode: string;
  email: string;
  firstName: string;
  lastName: string;
  phoneNumber: string | null;
  password: string;
}

export interface RegisterResponse {
  verification: VerificationChannel[];
  message: string;
}

export interface VerifyResponse {
  status: RegistrationStatus;
  remaining: VerificationChannel[];
}

export interface OrganisationSettings {
  id: string;
  name: string;
  slug: string;
  currency: string;
  template: string;
  selfRegistrationEnabled: boolean;
  selfRegistrationAllowedByPlatform: boolean;
  emailVerification: boolean;
  smsVerification: boolean;
}

export type EmailProvider = 'Log' | 'Smtp';
export type SmsProvider = 'Log' | 'Http';

export interface PlatformSettings {
  selfRegistrationEnabled: boolean;
  emailVerificationEnabled: boolean;
  smsVerificationEnabled: boolean;
  codeLength: number;
  codeTtlMinutes: number;
  maxAttempts: number;
  resendCooldownSeconds: number;
  emailProvider: EmailProvider;
  smtpHost: string | null;
  smtpPort: number;
  smtpUsername: string | null;
  smtpUseStartTls: boolean;
  emailFromAddress: string | null;
  emailFromName: string;
  smsProvider: SmsProvider;
  smsGatewayUrl: string | null;
  smsSenderId: string | null;
  smsAuthHeader: string;
  smsAuthScheme: string;
}

export interface PlatformSettingsView {
  settings: PlatformSettings;
  smtpPasswordConfigured: boolean;
  smsApiKeyConfigured: boolean;
}

export interface Grant {
  resource: string;
  action: string;
  scope: PermissionScope;
}

export interface RoleItem {
  id: string;
  name: string;
  description: string | null;
  isSystem: boolean;
  isAdministrator: boolean;
  permissions: Grant[];
  userCount: number;
  groupCount: number;
}

export interface GroupItem {
  id: string;
  name: string;
  description: string | null;
  members: NamedRef[];
  roles: NamedRef[];
}

export interface AutomationCondition {
  field: string;
  operator: ConditionOperator;
  value: string | null;
}

export interface AutomationAction {
  type: AutomationActionType;
  subject?: string | null;
  message?: string | null;
  dueInDays?: number | null;
  priority?: ActivityPriority | null;
  assignTo?: string | null;
  field?: string | null;
  value?: string | null;
}

export interface RuleItem {
  id: string;
  name: string;
  objectKey: string;
  trigger: AutomationTrigger;
  conditions: AutomationCondition[];
  actions: AutomationAction[];
  isActive: boolean;
  runCount: number;
  lastRunAt: string | null;
}

export interface AutomationField {
  key: string;
  label: string;
  settable: boolean;
}

export interface AuditItem {
  id: string;
  entityType: string;
  entityId: string;
  entityName: string | null;
  action: AuditAction;
  userId: string | null;
  userName: string | null;
  timestamp: string;
  changes: Record<string, [unknown, unknown]>;
}

export interface NotificationItem {
  id: string;
  title: string;
  message: string;
  link: string | null;
  createdAt: string;
  readAt: string | null;
}

export interface NotificationList {
  unreadCount: number;
  items: NotificationItem[];
}

export interface TenantItem {
  id: string;
  name: string;
  slug: string;
  status: TenantStatus;
  currency: string;
  template: string;
  createdAt: string;
  userCount: number;
}

export interface TemplateInfo {
  key: string;
  name: string;
  description: string;
}
