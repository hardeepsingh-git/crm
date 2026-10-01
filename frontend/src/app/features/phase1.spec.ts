import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { convertToParamMap, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AuthService } from '../core/auth.service';
import { MetadataService } from '../core/metadata.service';
import { FieldDef, ObjectDef } from '../core/models';
import { testUser } from '../core/testing';
import { CfFilters, cfFromQuery } from '../shared/custom-fields';
import { ListState } from '../shared/list';
import { FieldFormDialog } from './admin/object-editor';
import { RecordFormDialog, nameSourceField } from './records/record-form-dialog';
import { RelatedList, relationsTo } from './records/related-records';

const field = (key: string, type: FieldDef['type'], extra: Partial<FieldDef> = {}): FieldDef => ({
  id: key, key, label: key, type, isRequired: false, options: [], lookupObjectKey: null, maxLength: null, min: null, max: null,
  helpText: null, showInList: false, sortOrder: 0, isUnique: false, ...extra,
});

const object = (key: string, extra: Partial<ObjectDef> = {}): ObjectDef => ({
  key, label: key, pluralLabel: `${key}s`, icon: 'category', description: null, nameLabel: 'Name',
  isStandard: false, isActive: true, hasPipeline: false, sortOrder: 50, fields: [], stages: [], ...extra,
});

const candidate = object('candidate', { label: 'Candidate', pluralLabel: 'Candidates', nameLabel: 'Full name',
  fields: [field('email', 'Email', { isUnique: true, showInList: true })] });
const position = object('position', { label: 'Job Opening', pluralLabel: 'Job Openings', nameLabel: 'Job title' });
const application = object('application', {
  label: 'Application', pluralLabel: 'Applications', nameLabel: 'Candidate name', hasPipeline: true,
  stages: [{ key: 'applied', label: 'Applied', probability: 10, category: 'Open', color: '#1e88e5' }],
  fields: [
    field('candidate', 'Lookup', { label: 'Candidate', lookupObjectKey: 'candidate', isRequired: true, showInList: true, sortOrder: 0 }),
    field('position', 'Lookup', { label: 'Job opening', lookupObjectKey: 'position', isRequired: true, showInList: true, sortOrder: 1 }),
  ],
});
const ticket = object('ticket', { nameLabel: 'Subject', fields: [field('customer', 'Lookup', { lookupObjectKey: 'account' })] });
const objects = [candidate, position, application, ticket, object('account', { label: 'Company', isStandard: true })];

describe('Phase 1: recruitment core', () => {
  let backend: HttpTestingController;
  let dialogData: Record<string, unknown>;

  beforeEach(() => {
    dialogData = { objectKey: 'application' };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: MAT_DIALOG_DATA, useFactory: () => dialogData },
        { provide: MatDialogRef, useValue: { close: vi.fn() } },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
    TestBed.inject(MetadataService).data.set({ tenant: testUser().tenant, objects, picklists: {} });
    TestBed.inject(AuthService).user.set(testUser({ permissions: { '*': { read: 'All', create: 'All', update: 'All' } } }));
  });

  afterEach(() => backend.verify());

  function render<T>(component: Type<T>, inputs: Record<string, unknown> = {}): ComponentFixture<T> {
    const fixture = TestBed.createComponent(component);
    for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
    fixture.detectChanges();
    return fixture;
  }

  /** Answers every pending name lookup for a record, as the picker and the auto-name both ask for it. */
  function flushRecord(url: string, name: string): void {
    for (const req of backend.match(url)) req.flush({ id: url.split('/').pop(), name });
  }

  it('finds the objects that link to a record, only where the user can read them', () => {
    const toCandidate = relationsTo('candidate', objects, () => true);
    expect(toCandidate.map((r) => `${r.object.key}.${r.field.key}`)).toEqual(['application.candidate']);
    expect(relationsTo('position', objects, () => true).map((r) => r.field.key)).toEqual(['position']);
    expect(relationsTo('account', objects, () => true).map((r) => r.object.key)).toEqual(['ticket']);
    expect(relationsTo('candidate', objects, (key) => key !== 'application')).toEqual([]);
  });

  it('names a new record after the lookup its name label refers to', () => {
    const label = (key: string) => objects.find((o) => o.key === key)!.label;
    expect(nameSourceField(application, application.fields, label)?.key).toBe('candidate');
    expect(nameSourceField(ticket, ticket.fields, label)).toBeUndefined();
    expect(nameSourceField(position, [], label)).toBeUndefined();
  });

  it('prefills a new application and fills its name from the chosen candidate until the user types one', async () => {
    dialogData = { objectKey: 'application', defaults: { customFields: { position: 'p1' } } };
    const fixture = render(RecordFormDialog);
    const form = fixture.componentInstance;
    for (const req of backend.match('/api/users/lookup')) req.flush([]);
    flushRecord('/api/records/position/p1', 'Senior Developer');
    expect(form.cf.controls['position'].value).toBe('p1');
    expect(form.form.controls.name.value).toBe('');

    form.cf.controls['candidate'].setValue('c1');
    flushRecord('/api/records/candidate/c1', 'Jane Doe');
    expect(form.form.controls.name.value).toBe('Jane Doe');

    form.cf.controls['candidate'].setValue('c2');
    flushRecord('/api/records/candidate/c2', 'Raj Patel');
    expect(form.form.controls.name.value).toBe('Raj Patel');

    form.form.controls.name.setValue('Raj P. (referral)');
    form.cf.controls['candidate'].setValue('c3');
    flushRecord('/api/records/candidate/c3', 'Someone Else');
    expect(form.form.controls.name.value).toBe('Raj P. (referral)');
  });

  it('lists related records with links to the filtered list and pipeline', async () => {
    const relation = relationsTo('position', objects, () => true)[0];
    const fixture = render(RelatedList, { relation, recordId: 'p1', recordName: 'Senior Developer' });
    const req = backend.expectOne((r) => r.url === '/api/records/application');
    expect(req.request.params.get('cf.position')).toBe('p1');
    expect(req.request.params.get('sortBy')).toBe('updatedAt');
    req.flush({
      items: [{ id: 'a1', objectKey: 'application', name: 'Jane Doe', stage: 'applied', closedAt: null, ownerId: 'u1', ownerName: 'Ada',
        createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z', customFields: { candidate: 'c1', position: 'p1' } }],
      totalCount: 12, page: 1, pageSize: 10,
    });
    await fixture.whenStable();
    fixture.detectChanges();
    flushRecord('/api/records/candidate/c1', 'Jane Doe');
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Applications');
    expect(text).toContain('(12)');
    expect(text).toContain('Jane Doe');
    const links = Array.from(fixture.nativeElement.querySelectorAll('a')).map((a) => (a as HTMLAnchorElement).getAttribute('href'));
    expect(links).toContain('/o/application/board?cf.position=p1');
    expect(links).toContain('/o/application?cf.position=p1');
  });

  it('turns deep-link parameters into lookup filters and keeps the picker in sync', async () => {
    expect(cfFromQuery(convertToParamMap({ 'cf.position': 'p1', search: 'x', 'cf.': 'bad' }))).toEqual({ position: 'p1' });

    const state = TestBed.runInInjectionContext(() => new ListState());
    state.cf.set({ position: 'p1' });
    const fixture = render(CfFilters, { objectKey: 'application', state });
    const filters = fixture.componentInstance;
    flushRecord('/api/records/position/p1', 'Senior Developer');
    expect(filters.filterable().map((f) => f.key)).toEqual(['candidate', 'position']);

    filters.lookup('candidate').setValue('c9');
    expect(state.cf()).toEqual({ position: 'p1', candidate: 'c9' });
    state.cf.set({});
    fixture.detectChanges();
    expect(filters.lookup('position').value).toBeNull();
    for (const req of backend.match(() => true)) req.flush({});
  });

  it('offers the Unique option only for text-like fields', () => {
    dialogData = { objectKey: 'candidate', item: candidate.fields[0], nextSortOrder: 2 };
    const fixture = render(FieldFormDialog);
    expect(fixture.componentInstance.canBeUnique()).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Unique');
    expect(fixture.componentInstance.form.controls.isUnique.value).toBe(true);

    dialogData = { objectKey: 'candidate', nextSortOrder: 3 };
    const fresh = render(FieldFormDialog).componentInstance;
    fresh.form.controls.type.setValue('Number');
    expect(fresh.canBeUnique()).toBe(false);
  });
});
