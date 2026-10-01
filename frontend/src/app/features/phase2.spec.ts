import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AuthService } from '../core/auth.service';
import { MetadataService } from '../core/metadata.service';
import { EmailLogPage, ObjectDef, PermissionScope } from '../core/models';
import { testUser } from '../core/testing';
import { ActivityPanel } from '../shared/activity-panel';
import { EmailComposeDialog } from '../shared/email-compose';
import { RuleEditorDialog, describeSchedule, joinOffset, splitOffset } from './admin/automation-list';
import { EmailLog } from './admin/email-log';
import { insertAt } from './admin/email-templates';

const application: ObjectDef = {
  key: 'application', label: 'Application', pluralLabel: 'Applications', icon: 'assignment_ind', description: null,
  nameLabel: 'Candidate name', isStandard: false, isActive: true, hasPipeline: true, sortOrder: 1, stages: [],
  fields: [{
    id: 'f1', key: 'interview_date', label: 'Next interview', type: 'DateTime', isRequired: false, options: [], lookupObjectKey: null,
    maxLength: null, min: null, max: null, helpText: null, showInList: false, sortOrder: 0,
  }],
};

describe('Phase 2: email and date-based automation', () => {
  let backend: HttpTestingController;
  let dialogData: unknown;
  const close = vi.fn();

  function signIn(update: PermissionScope): void {
    TestBed.inject(AuthService).user.set(testUser({ permissions: { '*': { read: 'All', create: 'All', update } } }));
  }

  beforeEach(() => {
    close.mockReset();
    dialogData = null;
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: MAT_DIALOG_DATA, useFactory: () => dialogData },
        { provide: MatDialogRef, useValue: { close } },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
    TestBed.inject(MetadataService).data.set({ tenant: testUser().tenant, objects: [application], picklists: {} });
    signIn('All');
  });

  afterEach(() => backend.verify());

  function render<T>(component: Type<T>, inputs: Record<string, unknown> = {}): ComponentFixture<T> {
    const fixture = TestBed.createComponent(component);
    for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
    fixture.detectChanges();
    return fixture;
  }

  const text = (fixture: ComponentFixture<unknown>) => (fixture.nativeElement as HTMLElement).textContent ?? '';

  it('converts schedule offsets to and from minutes', () => {
    expect(splitOffset(-1440)).toEqual({ amount: 1, unit: 'days', direction: 'before' });
    expect(splitOffset(120)).toEqual({ amount: 2, unit: 'hours', direction: 'after' });
    expect(splitOffset(90)).toEqual({ amount: 90, unit: 'minutes', direction: 'after' });
    expect(joinOffset(1, 'days', 'before')).toBe(-1440);
    expect(joinOffset(3, 'hours', 'after')).toBe(180);
    expect(describeSchedule(-1440, 'Next interview')).toBe('1 day before Next interview');
    expect(describeSchedule(10080, 'Updated at')).toBe('7 days after Updated at');
    expect(describeSchedule(0, 'Closing date')).toBe('on Closing date');
  });

  it('inserts placeholders at the cursor, or at the end', () => {
    expect(insertAt('Hi ,', 3, '{{name}}')).toEqual({ value: 'Hi {{name}},', caret: 11 });
    expect(insertAt('Hi', null, ' {{name}}')).toEqual({ value: 'Hi {{name}}', caret: 11 });
  });

  it('composes an email to one of the record addresses, from a template, and sends it', () => {
    dialogData = { objectKey: 'application', recordId: 'r1' };
    const fixture = render(EmailComposeDialog);
    const dialog = fixture.componentInstance;

    const compose = backend.expectOne('/api/email/compose');
    expect(compose.request.body).toEqual({ objectKey: 'application', recordId: 'r1', templateId: null });
    compose.flush({ recipients: [{ label: 'Candidate → Email', address: 'mia@example.com' }], subject: '', body: '' });
    backend.expectOne((r) => r.url === '/api/email/templates').flush([{ id: 't1', name: 'Interview invitation', objectKey: 'application', subject: '', body: '', updatedAt: '' }]);
    fixture.detectChanges();
    expect(dialog.to).toBe('mia@example.com');

    dialog.templateId = 't1';
    dialog.applyTemplate();
    const templated = backend.expectOne('/api/email/compose');
    expect(templated.request.body.templateId).toBe('t1');
    templated.flush({ recipients: [], subject: 'Interview for Data Engineer', body: 'Hi Mia', blankFields: ['Next interview'] });
    expect([dialog.subject, dialog.body]).toEqual(['Interview for Data Engineer', 'Hi Mia']);
    fixture.detectChanges();
    expect(text(fixture)).toContain('Not filled in on this record: Next interview');

    dialog.send();
    const send = backend.expectOne('/api/email/send');
    expect(send.request.body).toEqual({ objectKey: 'application', recordId: 'r1', to: 'mia@example.com', subject: 'Interview for Data Engineer', body: 'Hi Mia', templateId: 't1' });
    send.flush({ id: 'e1', status: 'Pending' }, { status: 202, statusText: 'Accepted' });
    expect(close).toHaveBeenCalledWith(true);
  });

  it('explains when a record has no email address', () => {
    dialogData = { objectKey: 'application', recordId: 'r1' };
    const fixture = render(EmailComposeDialog);
    backend.expectOne('/api/email/compose').flush({ recipients: [], subject: '', body: '' });
    backend.expectOne((r) => r.url === '/api/email/templates').flush([]);
    fixture.detectChanges();
    expect(text(fixture)).toContain('This record has no email address');
  });

  it('offers Email on record timelines only to users who can update the object', () => {
    const inputs = { links: { customRecordId: 'r1' }, filterBy: 'customRecordId', customObjectKey: 'application' };
    const editor = render(ActivityPanel, inputs);
    backend.expectOne((r) => r.url === '/api/activities').flush({ items: [], totalCount: 0, page: 1, pageSize: 100 });
    expect(text(editor)).toContain('Email');

    signIn('None');
    const reader = render(ActivityPanel, inputs);
    backend.expectOne((r) => r.url === '/api/activities').flush({ items: [], totalCount: 0, page: 1, pageSize: 100 });
    expect(reader.componentInstance.emailObjectKey()).toBeNull();
  });

  it('saves a date-based rule that emails the candidate from a template', () => {
    const fixture = render(RuleEditorDialog);
    const editor = fixture.componentInstance;
    backend.expectOne('/api/admin/automation/fields/application').flush([
      { key: 'stage', label: 'Stage', settable: false, isDate: false },
      { key: 'cf.interview_date', label: 'Next interview', settable: true, isDate: true },
    ]);
    backend.expectOne('/api/email/fields/application').flush({ placeholders: [], recipients: [{ key: 'cf.candidate.cf.email', label: 'Candidate → Email' }] });
    backend.expectOne((r) => r.url === '/api/email/templates').flush([{ id: 't9', name: 'Interview invitation', objectKey: 'application', subject: 's', body: 'b', updatedAt: '' }]);
    for (const req of backend.match('/api/users/lookup')) req.flush([]);
    expect(editor.dateFields().map((f) => f.key)).toEqual(['cf.interview_date']);

    Object.assign(editor, { name: 'Interview prep', trigger: 'DateReached', scheduleField: 'cf.interview_date', offsetAmount: 1, offsetUnit: 'days', offsetDirection: 'before' });
    editor.actions[0] = { type: 'SendEmail', to: 'cf.candidate.cf.email', templateId: 't9', subject: 'ignored', assignTo: 'owner' };
    editor.save();

    const saved = backend.expectOne('/api/admin/automation');
    expect(saved.request.body).toMatchObject({
      name: 'Interview prep', trigger: 'DateReached', scheduleField: 'cf.interview_date', scheduleOffsetMinutes: -1440,
      actions: [{ type: 'SendEmail', to: 'cf.candidate.cf.email', templateId: 't9', subject: null, message: null, assignTo: null }],
    });
    saved.flush({});
  });

  it('shows delivery problems in the email log and lets admins retry', async () => {
    const fixture = render(EmailLog);
    const page: EmailLogPage = {
      deliveryMode: 'Log', dailyLimit: 1000, queuedToday: 3,
      page: { totalCount: 1, page: 1, pageSize: 25, items: [{
        id: 'e1', to: 'ann@example.com', subject: 'Your application', objectKey: 'application', recordId: 'r1', recordName: 'Ann Lee',
        ruleId: null, sentByName: 'Automation', status: 'Failed', attempts: 5, lastError: '451 Mailbox busy',
        createdAt: '2026-10-01T00:00:00Z', sentAt: null, nextAttemptAt: '2026-10-01T00:00:00Z',
      }] },
    };
    backend.expectOne((r) => r.url === '/api/admin/emails').flush(page);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(text(fixture)).toContain('No email provider is configured');
    expect(text(fixture)).toContain('Today: 3 of 1000');
    expect(text(fixture)).toContain('Ann Lee');

    const retry = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find((b) => b.textContent?.includes('Retry'))!;
    retry.click();
    backend.expectOne('/api/admin/emails/e1/retry').flush({});
    TestBed.tick();
    backend.expectOne((r) => r.url === '/api/admin/emails').flush(page);
  });
});
