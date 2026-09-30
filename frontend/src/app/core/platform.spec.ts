import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { customFieldsGroup, customFieldsValue } from '../shared/custom-fields';
import { ListState, withCurrent } from '../shared/list';
import { testUser } from './testing';
import { AuthService } from './auth.service';
import { MetadataService } from './metadata.service';
import { FieldDef, Metadata, ObjectDef } from './models';

const field = (key: string, type: FieldDef['type'], extra: Partial<FieldDef> = {}): FieldDef => ({
  id: key, key, label: key, type, isRequired: false, options: [], lookupObjectKey: null,
  maxLength: null, min: null, max: null, helpText: null, showInList: false, sortOrder: 0, ...extra,
});

const object = (key: string, extra: Partial<ObjectDef> = {}): ObjectDef => ({
  key, label: key, pluralLabel: `${key}s`, icon: 'category', description: null, nameLabel: 'Name',
  isStandard: false, isActive: true, hasPipeline: false, sortOrder: 50, fields: [], stages: [], ...extra,
});

describe('AuthService permissions', () => {
  let auth: AuthService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
  });

  it('applies Own, Group and All scopes to record ownership', () => {
    auth.user.set(testUser({
      teamUserIds: ['u1', 'u2'],
      permissions: { account: { read: 'All', update: 'Group', delete: 'Own' } },
    }));
    expect(auth.canAccess('account', 'read', 'stranger')).toBe(true);
    expect(auth.canModify('account', 'u2')).toBe(true);
    expect(auth.canModify('account', 'stranger')).toBe(false);
    expect(auth.canDelete('account', 'u1')).toBe(true);
    expect(auth.canDelete('account', 'u2')).toBe(false);
    expect(auth.can('account', 'create')).toBe(false);
  });

  it('lets "*" cover business objects unless a resolved object scope exists, and never admin areas', () => {
    auth.user.set(testUser({ permissions: { '*': { read: 'All', manage: 'All' }, lead: { read: 'Own' }, ticket: { read: 'None' } } }));
    expect(auth.scope('project', 'read')).toBe('All');
    expect(auth.scope('lead', 'read')).toBe('Own');
    expect(auth.can('ticket', 'read')).toBe(false);
    expect(auth.can('users', 'manage')).toBe(false);
    expect(auth.hasAdminAccess()).toBe(false);
  });

  it('only allows assigning other owners with Group or All update scope', () => {
    auth.user.set(testUser({ permissions: { lead: { update: 'Own' }, account: { update: 'Group' } } }));
    expect(auth.canAssign('lead')).toBe(false);
    expect(auth.canAssign('account')).toBe(true);
  });

  it('gives administrators everything and exposes admin areas', () => {
    auth.user.set(testUser({ isAdministrator: true }));
    expect(auth.scope('anything', 'delete')).toBe('All');
    expect(auth.hasAdminAccess()).toBe(true);
  });

  it('detects partial admin access from individual admin grants', () => {
    auth.user.set(testUser({ permissions: { audit: { read: 'All' } } }));
    expect(auth.hasAdminAccess()).toBe(true);
    expect(auth.can('roles', 'manage')).toBe(false);
  });
});

describe('MetadataService', () => {
  let meta: MetadataService;
  const data: Metadata = {
    tenant: { id: 't', name: 'Help Desk', slug: 'hd', currency: 'EUR', template: 'helpdesk' },
    objects: [
      object('account', { isStandard: true, label: 'Customer', pluralLabel: 'Customers' }),
      object('ticket', {
        hasPipeline: true,
        fields: [field('priority', 'Picklist', { options: ['Low', 'High'], showInList: true }), field('notes', 'TextArea')],
        stages: [
          { key: 'New', label: 'New', probability: 0, category: 'Open', color: '#1e88e5' },
          { key: 'Solved', label: 'Solved', probability: 100, category: 'Won', color: '#2e7d32' },
        ],
      }),
      object('asset', { isActive: false }),
    ],
    picklists: { 'lead.source': ['Web', 'Referral'] },
  };

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    meta = TestBed.inject(MetadataService);
  });

  it('loads metadata and resolves labels, routes and API URLs', async () => {
    const done = meta.load();
    TestBed.inject(HttpTestingController).expectOne('/api/metadata').flush(data);
    await done;

    expect(meta.currency()).toBe('EUR');
    expect(meta.label('account', true)).toBe('Customers');
    expect(meta.route('account', 'x')).toEqual(['/accounts', 'x']);
    expect(meta.route('ticket')).toEqual(['/o/ticket']);
    expect(meta.apiUrl('ticket')).toBe('/api/records/ticket');
    expect(meta.apiUrl('contact')).toBe('/api/contacts');
    expect(meta.activeObjects().map((o) => o.key)).toEqual(['account', 'ticket']);
    expect(meta.listFields('ticket').map((f) => f.key)).toEqual(['priority']);
    expect(meta.openStages('ticket').map((s) => s.key)).toEqual(['New']);
    expect(meta.picklist('lead.source')).toEqual(['Web', 'Referral']);
    expect(meta.picklist('unknown')).toEqual([]);
  });
});

describe('custom field forms', () => {
  const fields = [
    field('budget', 'Currency', { isRequired: true, min: 0 }),
    field('tags', 'MultiPicklist', { options: ['A', 'B'] }),
    field('vip', 'Boolean'),
    field('site', 'Url'),
    field('note', 'Text'),
  ];

  it('builds validated controls from field definitions', () => {
    const group = customFieldsGroup(fields, { note: 'hello' });
    expect(group.controls['budget'].hasError('required')).toBe(true);
    group.controls['budget'].setValue(-5);
    expect(group.controls['budget'].hasError('min')).toBe(true);
    group.controls['site'].setValue('not a url');
    expect(group.controls['site'].invalid).toBe(true);
    expect(group.controls['vip'].value).toBe(false);
    expect(group.controls['tags'].value).toEqual([]);
    expect(group.controls['note'].value).toBe('hello');
  });

  it('converts values for the API and sends null to clear empty fields', () => {
    const group = customFieldsGroup(fields);
    group.setValue({ budget: '1500', tags: [], vip: true, site: '', note: 'x' });
    expect(customFieldsValue(fields, group)).toEqual({ budget: 1500, tags: null, vip: true, site: null, note: 'x' });
  });
});

describe('list helpers', () => {
  it('keeps a legacy picklist value selectable', () => {
    expect(withCurrent(['A', 'B'], 'Z')).toEqual(['A', 'B', 'Z']);
    expect(withCurrent(['A', 'B'], 'A')).toEqual(['A', 'B']);
    expect(withCurrent(['A'], null)).toEqual(['A']);
  });

  it('sends custom-field filters as cf.<key> query parameters', () => {
    const state = TestBed.runInInjectionContext(() => new ListState());
    state.setCf('priority', 'High');
    state.setCf('vip', 'true');
    state.setCf('vip', null);
    const params = state.params();
    expect(params.get('cf.priority')).toBe('High');
    expect(params.has('cf.vip')).toBe(false);
    expect(params.get('page')).toBe('1');
  });
});
