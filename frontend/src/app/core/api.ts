import { HttpClient, HttpParams, httpResource } from '@angular/common/http';
import { Injectable, Resource, inject, linkedSignal } from '@angular/core';
import { Observable, map, of, shareReplay } from 'rxjs';
import {
  AccountDetail,
  AccountRequest,
  ActivityItem,
  AttachmentItem,
  ImportAnalysis,
  ImportResult,
  ActivityRequest,
  AuditItem,
  AutomationField,
  ComposeResult,
  ContactDetail,
  ContactRequest,
  EmailFieldSet,
  EmailTemplateItem,
  FieldDef,
  Grant,
  GroupItem,
  LeadConvertRequest,
  LeadConvertResult,
  LeadDetail,
  LeadRequest,
  ObjectDef,
  OpportunityDetail,
  OpportunityRequest,
  OrganisationSettings,
  OutboundEmailDetail,
  OutboundEmailItem,
  PlatformSettings,
  PlatformSettingsView,
  RecordItem,
  RecordRequest,
  RegisterRequest,
  RegisterResponse,
  RegistrationOptions,
  RoleItem,
  RuleItem,
  StageDef,
  TemplateInfo,
  TenantItem,
  UserItem,
  UserLookup,
  InterviewItem,
  InterviewRequest,
  InterviewSaved,
  ScorecardRequest,
  ErasureResult,
  OfferAnswered,
  OfferDefaults,
  OfferItem,
  OfferPreview,
  OfferRequest,
  OfferSent,
  RetentionRun,
  RetentionStatus,
  VerificationChannel,
  VerifyResponse,
} from './models';

export type QueryParams = Record<string, string | number | boolean | null | undefined>;

/** Drops empty values so they are not sent as query-string noise. */
export function toParams(query: QueryParams): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== null && value !== undefined && value !== '' && value !== false) params = params.set(key, String(value));
  }
  return params;
}

/** Hands a downloaded file to the browser as a download. */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url));
}

/** The file name in a Content-Disposition header (plain or RFC 5987), or the fallback. */
export function fileNameOf(disposition: string | null, fallback: string): string {
  const name = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition ?? '')?.[1];
  return name ? decodeURIComponent(name) : fallback;
}

/** Keeps the last successful value while a resource reloads, avoiding table flicker. */
export function stableValue<T>(resource: Resource<T | undefined>) {
  return linkedSignal<T | undefined, T | undefined>({
    source: () => (resource.hasValue() ? resource.value() : undefined),
    computation: (value, previous) => value ?? previous?.value,
  });
}

class ResourceApi<TDetail, TRequest> {
  constructor(
    private readonly http: HttpClient,
    readonly baseUrl: string,
  ) {}

  get(id: string): Observable<TDetail> {
    return this.http.get<TDetail>(`${this.baseUrl}/${id}`);
  }

  create(body: TRequest): Observable<TDetail> {
    return this.http.post<TDetail>(this.baseUrl, body);
  }

  update(id: string, body: TRequest): Observable<TDetail> {
    return this.http.put<TDetail>(`${this.baseUrl}/${id}`, body);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }
}

export interface UserRequest {
  email?: string;
  password?: string;
  firstName: string;
  lastName: string;
  isActive?: boolean;
  roleIds: string[];
  groupIds: string[];
}

export interface ObjectRequest {
  key?: string;
  label: string;
  pluralLabel: string;
  icon: string;
  description: string | null;
  nameLabel: string;
  hasPipeline: boolean;
  isActive: boolean;
  sortOrder: number;
}

export type FieldRequest = Omit<FieldDef, 'id'>;

export interface CreateTenantRequest {
  name: string;
  slug: string;
  template: string;
  currency: string;
  adminEmail: string;
  adminFirstName: string;
  adminLastName: string;
  adminPassword: string;
  seedDemoData: boolean;
}

@Injectable({ providedIn: 'root' })
export class CrmApi {
  private readonly http = inject(HttpClient);

  readonly accounts = new ResourceApi<AccountDetail, AccountRequest>(this.http, '/api/accounts');
  readonly contacts = new ResourceApi<ContactDetail, ContactRequest>(this.http, '/api/contacts');
  readonly leads = new ResourceApi<LeadDetail, LeadRequest>(this.http, '/api/leads');
  readonly opportunities = new ResourceApi<OpportunityDetail, OpportunityRequest>(this.http, '/api/opportunities');
  readonly activities = new ResourceApi<ActivityItem, ActivityRequest>(this.http, '/api/activities');

  records(objectKey: string): ResourceApi<RecordItem, RecordRequest> {
    return new ResourceApi<RecordItem, RecordRequest>(this.http, `/api/records/${objectKey}`);
  }

  convertLead(id: string, body: LeadConvertRequest): Observable<LeadConvertResult> {
    return this.http.post<LeadConvertResult>(`/api/leads/${id}/convert`, body);
  }

  changeStage(id: string, stage: string, lossReason: string | null = null): Observable<OpportunityDetail> {
    return this.http.patch<OpportunityDetail>(`/api/opportunities/${id}/stage`, { stage, lossReason });
  }

  changeRecordStage(objectKey: string, id: string, stage: string): Observable<RecordItem> {
    return this.http.patch<RecordItem>(`/api/records/${objectKey}/${id}/stage`, { stage });
  }

  setActivityCompleted(id: string, completed: boolean): Observable<ActivityItem> {
    return this.http.post<ActivityItem>(`/api/activities/${id}/${completed ? 'complete' : 'reopen'}`, null);
  }

  // ---------- Users, roles, groups ----------

  createUser(body: UserRequest): Observable<UserItem> {
    return this.http.post<UserItem>('/api/users', body);
  }

  updateUser(id: string, body: UserRequest): Observable<UserItem> {
    return this.http.put<UserItem>(`/api/users/${id}`, body);
  }

  resetPassword(id: string, newPassword: string): Observable<void> {
    return this.http.post<void>(`/api/users/${id}/reset-password`, { newPassword });
  }

  approveUser(id: string, body: { roleIds: string[]; groupIds: string[] }): Observable<UserItem> {
    return this.http.post<UserItem>(`/api/users/${id}/approve`, body);
  }

  rejectUser(id: string): Observable<UserItem> {
    return this.http.post<UserItem>(`/api/users/${id}/reject`, {});
  }

  // ---------- Password reset (anonymous) ----------

  forgotPassword(email: string): Observable<void> {
    return this.http.post<void>('/api/auth/forgot-password', { email });
  }

  resetPasswordWithToken(token: string, newPassword: string): Observable<void> {
    return this.http.post<void>('/api/auth/reset-password', { token, newPassword });
  }

  // ---------- Self-registration (anonymous) ----------

  registrationOptions(tenantCode: string): Observable<RegistrationOptions> {
    return this.http.get<RegistrationOptions>('/api/auth/registration-options', { params: { tenant: tenantCode } });
  }

  register(body: RegisterRequest): Observable<RegisterResponse> {
    return this.http.post<RegisterResponse>('/api/auth/register', body);
  }

  verify(email: string, channel: VerificationChannel, code: string): Observable<VerifyResponse> {
    return this.http.post<VerifyResponse>('/api/auth/verify', { email, channel, code });
  }

  resendCode(email: string, channel: VerificationChannel): Observable<void> {
    return this.http.post<void>('/api/auth/resend-code', { email, channel });
  }

  saveRole(id: string | null, body: { name: string; description: string | null; permissions: Grant[] }): Observable<RoleItem> {
    return id ? this.http.put<RoleItem>(`/api/admin/roles/${id}`, body) : this.http.post<RoleItem>('/api/admin/roles', body);
  }

  deleteRole(id: string): Observable<void> {
    return this.http.delete<void>(`/api/admin/roles/${id}`);
  }

  saveGroup(id: string | null, body: { name: string; description: string | null; memberIds: string[]; roleIds: string[] }): Observable<GroupItem> {
    return id ? this.http.put<GroupItem>(`/api/admin/groups/${id}`, body) : this.http.post<GroupItem>('/api/admin/groups', body);
  }

  deleteGroup(id: string): Observable<void> {
    return this.http.delete<void>(`/api/admin/groups/${id}`);
  }

  // ---------- Customization ----------

  saveObject(key: string | null, body: ObjectRequest): Observable<ObjectDef> {
    return key ? this.http.put<ObjectDef>(`/api/admin/objects/${key}`, body) : this.http.post<ObjectDef>('/api/admin/objects', body);
  }

  deleteObject(key: string): Observable<void> {
    return this.http.delete<void>(`/api/admin/objects/${key}`);
  }

  saveField(objectKey: string, existingKey: string | null, body: FieldRequest): Observable<FieldDef> {
    return existingKey
      ? this.http.put<FieldDef>(`/api/admin/objects/${objectKey}/fields/${existingKey}`, body)
      : this.http.post<FieldDef>(`/api/admin/objects/${objectKey}/fields`, body);
  }

  deleteField(objectKey: string, fieldKey: string): Observable<void> {
    return this.http.delete<void>(`/api/admin/objects/${objectKey}/fields/${fieldKey}`);
  }

  saveStages(objectKey: string, stages: StageDef[]): Observable<ObjectDef> {
    return this.http.put<ObjectDef>(`/api/admin/objects/${objectKey}/stages`, { stages });
  }

  savePicklist(key: string, options: string[]): Observable<string[]> {
    return this.http.put<string[]>(`/api/admin/picklists/${key}`, { options });
  }

  automationFields(objectKey: string): Observable<AutomationField[]> {
    return this.http.get<AutomationField[]>(`/api/admin/automation/fields/${objectKey}`);
  }

  saveRule(id: string | null, body: Omit<RuleItem, 'id' | 'runCount' | 'lastRunAt'>): Observable<RuleItem> {
    return id ? this.http.put<RuleItem>(`/api/admin/automation/${id}`, body) : this.http.post<RuleItem>('/api/admin/automation', body);
  }

  deleteRule(id: string): Observable<void> {
    return this.http.delete<void>(`/api/admin/automation/${id}`);
  }

  // ---------- Email ----------

  emailFields(objectKey: string): Observable<EmailFieldSet> {
    return this.http.get<EmailFieldSet>(`/api/email/fields/${objectKey}`);
  }

  emailTemplatesFor(objectKey: string): Observable<EmailTemplateItem[]> {
    return this.http.get<EmailTemplateItem[]>('/api/email/templates', { params: { objectKey } });
  }

  composeEmail(objectKey: string, recordId: string, templateId: string | null = null): Observable<ComposeResult> {
    return this.http.post<ComposeResult>('/api/email/compose', { objectKey, recordId, templateId });
  }

  sendEmail(body: { objectKey: string; recordId: string; to: string; subject: string; body: string; templateId: string | null }): Observable<{ id: string; status: string }> {
    return this.http.post<{ id: string; status: string }>('/api/email/send', body);
  }

  saveEmailTemplate(id: string | null, body: { name: string; objectKey: string; subject: string; body: string }): Observable<EmailTemplateItem> {
    return id
      ? this.http.put<EmailTemplateItem>(`/api/admin/email-templates/${id}`, body)
      : this.http.post<EmailTemplateItem>('/api/admin/email-templates', body);
  }

  deleteEmailTemplate(id: string): Observable<void> {
    return this.http.delete<void>(`/api/admin/email-templates/${id}`);
  }

  emailDetail(id: string): Observable<OutboundEmailDetail> {
    return this.http.get<OutboundEmailDetail>(`/api/admin/emails/${id}`);
  }

  retryEmail(id: string): Observable<OutboundEmailItem> {
    return this.http.post<OutboundEmailItem>(`/api/admin/emails/${id}/retry`, null);
  }

  cancelEmail(id: string): Observable<OutboundEmailItem> {
    return this.http.post<OutboundEmailItem>(`/api/admin/emails/${id}/cancel`, null);
  }

  saveSettings(body: { name: string; currency: string; selfRegistrationEnabled?: boolean; careersEnabled?: boolean; careersIntro?: string | null }): Observable<OrganisationSettings> {
    return this.http.put<OrganisationSettings>('/api/settings', body);
  }

  // ---------- Careers (anonymous) ----------

  applyForJob(slug: string, jobId: string, form: FormData): Observable<{ message: string }> {
    return this.http.post<{ message: string }>(`/api/careers/${encodeURIComponent(slug)}/jobs/${jobId}/apply`, form);
  }

  // ---------- Export ----------

  /** Downloads a CSV export of an object's list with the list's current filters. */
  exportCsv(objectKey: string, params: HttpParams): Observable<void> {
    return this.http
      .get(`/api/export/${objectKey}`, { params: params.delete('page').delete('pageSize'), responseType: 'blob', observe: 'response' })
      .pipe(
        map((response) => {
          const name = fileNameOf(response.headers.get('Content-Disposition'), `${objectKey}.csv`);
          saveBlob(response.body!, name);
        }),
      );
  }

  recordHistory(entityType: string, id: string): Observable<AuditItem[]> {
    return this.http.get<AuditItem[]>(`/api/audit/${entityType}/${id}`);
  }

  // ---------- Notifications ----------

  markNotificationRead(id: string): Observable<void> {
    return this.http.post<void>(`/api/notifications/${id}/read`, null);
  }

  markAllNotificationsRead(): Observable<void> {
    return this.http.post<void>('/api/notifications/read-all', null);
  }

  // ---------- Platform ----------

  templates(): Observable<TemplateInfo[]> {
    return this.http.get<TemplateInfo[]>('/api/platform/templates');
  }

  createTenant(body: CreateTenantRequest): Observable<TenantItem> {
    return this.http.post<TenantItem>('/api/platform/tenants', body);
  }

  updateTenant(id: string, body: { name: string; currency: string; status: string }): Observable<TenantItem> {
    return this.http.put<TenantItem>(`/api/platform/tenants/${id}`, body);
  }

  deleteTenant(id: string): Observable<void> {
    return this.http.delete<void>(`/api/platform/tenants/${id}`);
  }

  savePlatformSettings(settings: PlatformSettings): Observable<PlatformSettingsView> {
    return this.http.put<PlatformSettingsView>('/api/platform/settings', settings);
  }

  testMessage(channel: VerificationChannel, to: string): Observable<void> {
    return this.http.post<void>(`/api/platform/settings/test-${channel === 'Email' ? 'email' : 'sms'}`, { to });
  }

  // ---------- Attachments ----------

  uploadAttachment(objectKey: string, recordId: string, file: File): Observable<AttachmentItem> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.http.post<AttachmentItem>('/api/attachments', form, { params: { objectKey, recordId } });
  }

  /** Fetched as a blob because a plain link can't carry the bearer token. */
  downloadAttachment(id: string): Observable<Blob> {
    return this.http.get(`/api/attachments/${id}/content`, { responseType: 'blob' });
  }

  deleteAttachment(id: string): Observable<void> {
    return this.http.delete<void>(`/api/attachments/${id}`);
  }

  // ---------- CSV import ----------

  analyzeImport(objectKey: string, csv: string): Observable<ImportAnalysis> {
    return this.http.post<ImportAnalysis>(`/api/import/${objectKey}/analyze`, { csv });
  }

  runImport(objectKey: string, body: { csv: string; mapping: Record<string, string | null>; dryRun: boolean; runAutomation: boolean }): Observable<ImportResult> {
    return this.http.post<ImportResult>(`/api/import/${objectKey}`, body);
  }

  // ---------- Interviews ----------

  saveInterview(id: string | null, body: InterviewRequest): Observable<InterviewSaved> {
    return id ? this.http.put<InterviewSaved>(`/api/interviews/${id}`, body) : this.http.post<InterviewSaved>('/api/interviews', body);
  }

  setInterviewStatus(id: string, action: 'cancel' | 'complete'): Observable<InterviewItem> {
    return this.http.post<InterviewItem>(`/api/interviews/${id}/${action}`, null);
  }

  submitScorecard(id: string, body: ScorecardRequest): Observable<InterviewItem> {
    return this.http.put<InterviewItem>(`/api/interviews/${id}/scorecard`, body);
  }

  downloadInterviewCalendar(id: string): Observable<void> {
    return this.http.get(`/api/interviews/${id}/ics`, { responseType: 'blob' }).pipe(map((blob) => saveBlob(blob, 'interview.ics')));
  }

  // ---------- Offers ----------

  offerDefaults(applicationId: string): Observable<OfferDefaults> {
    return this.http.get<OfferDefaults>('/api/offers/defaults', { params: { applicationId } });
  }

  saveOffer(id: string | null, body: OfferRequest): Observable<OfferItem> {
    return id ? this.http.put<OfferItem>(`/api/offers/${id}`, body) : this.http.post<OfferItem>('/api/offers', body);
  }

  deleteOffer(id: string): Observable<void> {
    return this.http.delete<void>(`/api/offers/${id}`);
  }

  previewOffer(id: string): Observable<OfferPreview> {
    return this.http.get<OfferPreview>(`/api/offers/${id}/preview`);
  }

  sendOffer(id: string): Observable<OfferSent> {
    return this.http.post<OfferSent>(`/api/offers/${id}/send`, null);
  }

  withdrawOffer(id: string, reason: string | null): Observable<OfferItem> {
    return this.http.post<OfferItem>(`/api/offers/${id}/withdraw`, { reason });
  }

  // ---------- Offer response (anonymous) ----------

  answerOffer(token: string, answer: 'accept', body: { signedName: string; agree: boolean }): Observable<OfferAnswered>;
  answerOffer(token: string, answer: 'decline', body: { reason: string | null }): Observable<OfferAnswered>;
  answerOffer(token: string, answer: 'accept' | 'decline', body: object): Observable<OfferAnswered> {
    return this.http.post<OfferAnswered>(`/api/offer-response/${encodeURIComponent(token)}/${answer}`, body);
  }

  // ---------- Privacy ----------

  exportCandidateData(id: string): Observable<void> {
    return this.http
      .get(`/api/privacy/candidates/${id}/export`, { responseType: 'blob', observe: 'response' })
      .pipe(map((response) => saveBlob(response.body!, fileNameOf(response.headers.get('Content-Disposition'), 'candidate-data.zip'))));
  }

  eraseCandidate(id: string, confirmName: string): Observable<ErasureResult> {
    return this.http.post<ErasureResult>(`/api/privacy/candidates/${id}/erase`, { confirmName });
  }

  saveRetention(months: number | null): Observable<RetentionStatus> {
    return this.http.put<RetentionStatus>('/api/privacy/retention', { months });
  }

  runRetention(): Observable<RetentionRun> {
    return this.http.post<RetentionRun>('/api/privacy/retention/run', null);
  }
}

@Injectable({ providedIn: 'root' })
export class UserLookupService {
  readonly users = httpResource<UserLookup[]>(() => '/api/users/lookup', { defaultValue: [] });
}

/** Resolves record ids (lookup fields) to display names, cached for the session. */
@Injectable({ providedIn: 'root' })
export class RecordNameService {
  private readonly http = inject(HttpClient);
  private readonly cache = new Map<string, Observable<string>>();

  name(url: string | null): Observable<string> {
    if (!url) return of('');
    let cached = this.cache.get(url);
    if (!cached) {
      cached = this.http.get<{ name?: string; fullName?: string; subject?: string }>(url).pipe(
        map((r) => r.name ?? r.fullName ?? r.subject ?? ''),
        shareReplay(1),
      );
      this.cache.set(url, cached);
    }
    return cached;
  }
}
