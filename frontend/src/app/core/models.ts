export type LeadSource = 'Web' | 'Referral' | 'Event' | 'ColdCall' | 'Advertisement' | 'Partner' | 'Other';
export type LeadStatus = 'New' | 'Contacted' | 'Qualified' | 'Unqualified' | 'Converted';
export type OpportunityStage = 'Prospecting' | 'Qualification' | 'Proposal' | 'Negotiation' | 'ClosedWon' | 'ClosedLost';
export type ActivityType = 'Task' | 'Call' | 'Meeting' | 'Email' | 'Note';
export type ActivityPriority = 'Low' | 'Normal' | 'High';
export type Role = 'Admin' | 'Manager' | 'SalesRep';

export const LEAD_SOURCES: LeadSource[] = ['Web', 'Referral', 'Event', 'ColdCall', 'Advertisement', 'Partner', 'Other'];
export const LEAD_STATUSES: LeadStatus[] = ['New', 'Contacted', 'Qualified', 'Unqualified', 'Converted'];
export const OPEN_STAGES: OpportunityStage[] = ['Prospecting', 'Qualification', 'Proposal', 'Negotiation'];
export const STAGES: OpportunityStage[] = [...OPEN_STAGES, 'ClosedWon', 'ClosedLost'];
export const ACTIVITY_TYPES: ActivityType[] = ['Task', 'Call', 'Meeting', 'Email', 'Note'];
export const PRIORITIES: ActivityPriority[] = ['Low', 'Normal', 'High'];
export const ROLES: Role[] = ['Admin', 'Manager', 'SalesRep'];

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface UserInfo {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  fullName: string;
  roles: Role[];
}

export interface AuthResponse {
  accessToken: string;
  expiresAt: string;
  refreshToken: string;
  user: UserInfo;
}

interface Owned {
  id: string;
  ownerId: string;
  ownerName: string;
  createdAt: string;
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
}

export interface LeadListItem extends Owned {
  firstName: string;
  lastName: string;
  fullName: string;
  company: string;
  email: string | null;
  phone: string | null;
  status: LeadStatus;
  source: LeadSource;
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
  source: LeadSource;
  status: LeadStatus;
  estimatedValue: number | null;
  description: string | null;
  ownerId: string | null;
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
  stage: OpportunityStage;
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
  stage: OpportunityStage;
  probability: number | null;
  expectedCloseDate: string;
  lossReason: string | null;
  description: string | null;
  ownerId: string | null;
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
  ownerId: string | null;
}

export interface ActivityLinks {
  accountId?: string | null;
  contactId?: string | null;
  leadId?: string | null;
  opportunityId?: string | null;
}

export interface DashboardSummary {
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
  pipelineByStage: { stage: OpportunityStage; count: number; amount: number }[];
  leadsBySource: { source: LeadSource; count: number }[];
  wonByMonth: { year: number; month: number; count: number; amount: number }[];
  upcomingActivities: ActivityItem[];
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
  roles: Role[];
  createdAt: string;
  lockoutEnd: string | null;
}
