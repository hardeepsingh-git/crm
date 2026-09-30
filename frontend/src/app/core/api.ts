import { HttpClient, HttpParams, httpResource } from '@angular/common/http';
import { Injectable, Resource, inject, linkedSignal } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AccountDetail,
  AccountRequest,
  ActivityItem,
  ActivityRequest,
  ContactDetail,
  ContactRequest,
  LeadConvertRequest,
  LeadConvertResult,
  LeadDetail,
  LeadRequest,
  OpportunityDetail,
  OpportunityRequest,
  OpportunityStage,
  UserItem,
  UserLookup,
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

@Injectable({ providedIn: 'root' })
export class CrmApi {
  private readonly http = inject(HttpClient);

  readonly accounts = new ResourceApi<AccountDetail, AccountRequest>(this.http, '/api/accounts');
  readonly contacts = new ResourceApi<ContactDetail, ContactRequest>(this.http, '/api/contacts');
  readonly leads = new ResourceApi<LeadDetail, LeadRequest>(this.http, '/api/leads');
  readonly opportunities = new ResourceApi<OpportunityDetail, OpportunityRequest>(this.http, '/api/opportunities');
  readonly activities = new ResourceApi<ActivityItem, ActivityRequest>(this.http, '/api/activities');

  convertLead(id: string, body: LeadConvertRequest): Observable<LeadConvertResult> {
    return this.http.post<LeadConvertResult>(`/api/leads/${id}/convert`, body);
  }

  changeStage(id: string, stage: OpportunityStage, lossReason: string | null = null): Observable<OpportunityDetail> {
    return this.http.patch<OpportunityDetail>(`/api/opportunities/${id}/stage`, { stage, lossReason });
  }

  setActivityCompleted(id: string, completed: boolean): Observable<ActivityItem> {
    return this.http.post<ActivityItem>(`/api/activities/${id}/${completed ? 'complete' : 'reopen'}`, null);
  }

  createUser(body: { email: string; firstName: string; lastName: string; password: string; roles: string[] }): Observable<UserItem> {
    return this.http.post<UserItem>('/api/users', body);
  }

  updateUser(id: string, body: { firstName: string; lastName: string; isActive: boolean; roles: string[] }): Observable<UserItem> {
    return this.http.put<UserItem>(`/api/users/${id}`, body);
  }

  resetPassword(id: string, newPassword: string): Observable<void> {
    return this.http.post<void>(`/api/users/${id}/reset-password`, { newPassword });
  }
}

@Injectable({ providedIn: 'root' })
export class UserLookupService {
  readonly users = httpResource<UserLookup[]>(() => '/api/users/lookup', { defaultValue: [] });
}
