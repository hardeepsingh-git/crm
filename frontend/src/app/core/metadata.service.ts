import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { FieldDef, Metadata, ObjectDef, StageDef } from './models';

const STANDARD_ROUTES: Record<string, string> = {
  account: '/accounts',
  contact: '/contacts',
  lead: '/leads',
  opportunity: '/opportunities',
  activity: '/activities',
};

const STANDARD_API: Record<string, string> = {
  account: '/api/accounts',
  contact: '/api/contacts',
  lead: '/api/leads',
  opportunity: '/api/opportunities',
  activity: '/api/activities',
};

/** The tenant's configuration: objects, fields, stages, picklists and currency. Loaded once per session. */
@Injectable({ providedIn: 'root' })
export class MetadataService {
  private readonly http = inject(HttpClient);
  readonly data = signal<Metadata | null>(null);

  readonly objects = computed(() => this.data()?.objects ?? []);
  readonly activeObjects = computed(() => this.objects().filter((o) => o.isActive));
  readonly customObjects = computed(() => this.objects().filter((o) => !o.isStandard));
  readonly currency = computed(() => this.data()?.tenant.currency ?? 'USD');
  readonly tenantName = computed(() => this.data()?.tenant.name ?? '');

  async load(): Promise<void> {
    this.data.set(await firstValueFrom(this.http.get<Metadata>('/api/metadata')));
  }

  clear(): void {
    this.data.set(null);
  }

  object(key: string): ObjectDef | undefined {
    return this.objects().find((o) => o.key === key);
  }

  isActive(key: string): boolean {
    return this.object(key)?.isActive ?? false;
  }

  label(key: string, plural = false): string {
    const o = this.object(key);
    return (plural ? o?.pluralLabel : o?.label) ?? key;
  }

  fields(key: string): FieldDef[] {
    return this.object(key)?.fields ?? [];
  }

  listFields(key: string): FieldDef[] {
    return this.fields(key).filter((f) => f.showInList);
  }

  stages(key: string): StageDef[] {
    return this.object(key)?.stages ?? [];
  }

  openStages(key: string): StageDef[] {
    return this.stages(key).filter((s) => s.category === 'Open');
  }

  stage(key: string, stageKey: string): StageDef | undefined {
    return this.stages(key).find((s) => s.key === stageKey);
  }

  picklist(key: string): string[] {
    return this.data()?.picklists[key] ?? [];
  }

  /** UI route for an object's list, or a record when an id is given. */
  route(key: string, id?: string): string[] {
    const base = STANDARD_ROUTES[key] ?? `/o/${key}`;
    return id ? [base, id] : [base];
  }

  apiUrl(key: string): string {
    return STANDARD_API[key] ?? `/api/records/${key}`;
  }
}
