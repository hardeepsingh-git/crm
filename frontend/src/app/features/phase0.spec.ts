import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AuthService } from '../core/auth.service';
import { ReportingErrorHandler } from '../core/error-reporting';
import { MetadataService } from '../core/metadata.service';
import { ImportAnalysis, ObjectDef } from '../core/models';
import { testUser } from '../core/testing';
import { AttachmentsPanel, checkAttachment, formatBytes } from '../shared/attachments-panel';
import { ForgotPassword, ResetPassword } from './auth/password-reset';
import { ImportDialog, csvCell } from './records/import-dialog';

const position: ObjectDef = {
  key: 'position', label: 'Job Opening', pluralLabel: 'Job Openings', icon: 'work', description: null, nameLabel: 'Job title',
  isStandard: false, isActive: true, hasPipeline: false, sortOrder: 10, stages: [],
  fields: [{ id: 'f1', key: 'status', label: 'Status', type: 'Picklist', isRequired: true, options: ['Open'], lookupObjectKey: null,
    maxLength: null, min: null, max: null, helpText: null, showInList: true, sortOrder: 1 }],
};

describe('Phase 0 UI', () => {
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: MAT_DIALOG_DATA, useValue: { objectKey: 'position' } },
        { provide: MatDialogRef, useValue: { close: vi.fn() } },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  function render<T>(component: Type<T>, inputs: Record<string, unknown> = {}): ComponentFixture<T> {
    const fixture = TestBed.createComponent(component);
    for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
    fixture.detectChanges();
    return fixture;
  }

  async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const text = (fixture: ComponentFixture<unknown>) => fixture.nativeElement.textContent as string;

  describe('password reset', () => {
    it('requests a link with Enter and never says whether the account exists', async () => {
      const fixture = render(ForgotPassword);
      const input: HTMLInputElement = fixture.nativeElement.querySelector('input');
      input.value = ' rita@example.com ';
      input.dispatchEvent(new Event('input'));
      const submit = new Event('submit', { cancelable: true });
      fixture.nativeElement.querySelector('form').dispatchEvent(submit);
      expect(submit.defaultPrevented).toBe(true);

      const req = backend.expectOne('/api/auth/forgot-password');
      expect(req.request.body).toEqual({ email: 'rita@example.com' });
      req.flush(null, { status: 202, statusText: 'Accepted' });
      await settle(fixture);
      expect(text(fixture)).toContain("If an account exists");
    });

    it('sets a new password from the emailed token and explains expired links', async () => {
      expect(text(render(ResetPassword))).toContain('This link is incomplete');

      const fixture = render(ResetPassword, { token: 'tok123' });
      const page = fixture.componentInstance;
      page.form.setValue({ password: 'New#Pass12345', confirm: 'Other#Pass1234' });
      page.submit();
      backend.expectNone('/api/auth/reset-password');

      page.form.controls.confirm.setValue('New#Pass12345');
      page.submit();
      const failed = backend.expectOne('/api/auth/reset-password');
      expect(failed.request.body).toEqual({ token: 'tok123', newPassword: 'New#Pass12345' });
      failed.flush({ errors: { token: ['This reset link is invalid or has expired. Request a new one.'] } }, { status: 400, statusText: 'Bad Request' });
      fixture.detectChanges();
      expect(text(fixture)).toContain('invalid or has expired');
      expect(text(fixture)).toContain('Request a new link');

      page.submit();
      backend.expectOne('/api/auth/reset-password').flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      expect(text(fixture)).toContain('signed out on all devices');
    });
  });

  describe('attachments', () => {
    it('lists files and offers upload only to users who can edit the record', async () => {
      const auth = TestBed.inject(AuthService);
      auth.user.set(testUser({ permissions: { account: { read: 'All', update: 'Own' } } }));

      const own = render(AttachmentsPanel, { objectKey: 'account', recordId: 'r1', ownerId: 'u1' });
      const req = backend.expectOne((r) => r.url === '/api/attachments');
      expect(req.request.params.get('objectKey')).toBe('account');
      expect(req.request.params.get('recordId')).toBe('r1');
      req.flush([{ id: 'a1', fileName: 'Jane CV.pdf', contentType: 'application/pdf', size: 2048, uploadedById: 'u1', uploadedByName: 'Ada', createdAt: '2026-10-01T00:00:00Z' }]);
      await settle(own);
      expect(text(own)).toContain('Jane CV.pdf');
      expect(text(own)).toContain('2 KB');
      expect(own.nativeElement.querySelector('input[type=file]')).not.toBeNull();

      const other = render(AttachmentsPanel, { objectKey: 'account', recordId: 'r2', ownerId: 'someone-else' });
      backend.expectOne((r) => r.url === '/api/attachments').flush([]);
      await settle(other);
      expect(other.nativeElement.querySelector('input[type=file]')).toBeNull();
      expect(text(other)).toContain('No files yet.');
    });

    it('pre-checks type and size like the server', () => {
      const file = (name: string, size: number) => new File([new Uint8Array(size)], name);
      expect(checkAttachment(file('cv.PDF', 10))).toBeNull();
      expect(checkAttachment(file('run.exe', 10))).toContain("isn't allowed");
      expect(checkAttachment(file('empty.pdf', 0))).toContain('empty');
      expect(checkAttachment(file('big.pdf', 11 * 1024 * 1024))).toContain('larger than 10 MB');
      expect(formatBytes(512)).toBe('512 B');
      expect(formatBytes(3.5 * 1024 * 1024)).toBe('3.5 MB');
    });
  });

  describe('CSV import', () => {
    const analysis: ImportAnalysis = {
      headers: ['Title', 'Status'], rowCount: 2, sample: [['Developer', 'Open'], ['Analyst', 'Open']],
      suggestedMapping: { Title: null, Status: 'cf.status' },
      targets: [
        { key: 'name', label: 'Job title', type: 'Text', required: true, help: null },
        { key: 'cf.status', label: 'Status', type: 'Picklist', required: true, help: null },
      ],
    };

    it('maps columns, checks every row, shows row errors and imports when clean', async () => {
      TestBed.inject(MetadataService).data.set({ tenant: testUser().tenant, objects: [position], picklists: {} });
      const fixture = render(ImportDialog);
      const dialog = fixture.componentInstance;
      const picker = { files: [new File(['Title,Status\nDeveloper,Open\nAnalyst,Open\n'], 'jobs.csv')], value: 'x' } as unknown as HTMLInputElement;

      await dialog.choose(picker);
      const analyze = backend.expectOne('/api/import/position/analyze');
      expect(analyze.request.body.csv).toContain('Developer');
      analyze.flush(analysis);
      fixture.detectChanges();
      expect(dialog.step()).toBe('map');
      expect(dialog.missing()).toEqual(['Job title']);
      expect(text(fixture)).toContain('Required: Job title');

      dialog.setTarget('Title', 'name');
      expect(dialog.missing()).toEqual([]);
      expect(dialog.takenBy('name', 'Status')).toBe(true);

      dialog.check();
      const dry = backend.expectOne('/api/import/position');
      expect(dry.request.body).toMatchObject({ dryRun: true, runAutomation: false, mapping: { Title: 'name', Status: 'cf.status' } });
      dry.flush({ dryRun: true, total: 2, created: 0, errorCount: 1, errors: [{ row: 3, messages: ['Status: Must be one of: Open.'] }] });
      fixture.detectChanges();
      expect(text(fixture)).toContain('Row 3:');
      expect(text(fixture)).toContain('Nothing has been imported');

      dialog.check();
      backend.expectOne('/api/import/position').flush({ dryRun: true, total: 2, created: 0, errorCount: 0, errors: [] });
      fixture.detectChanges();
      expect(text(fixture)).toContain('All 2 rows are valid');

      dialog.import();
      const run = backend.expectOne('/api/import/position');
      expect(run.request.body.dryRun).toBe(false);
      run.flush({ dryRun: false, total: 2, created: 2, errorCount: 0, errors: [] });
      fixture.detectChanges();
      expect(dialog.step()).toBe('done');
      expect(text(fixture)).toContain('Imported 2 job openings');
    });

    it('quotes template cells only when needed', () => {
      expect(csvCell('Job title')).toBe('Job title');
      expect(csvCell('Rating (1-5), stars')).toBe('"Rating (1-5), stars"');
      expect(csvCell('Say "hi"')).toBe('"Say ""hi"""');
    });
  });

  describe('browser error reporting', () => {
    it('reports unexpected errors once, and skips HTTP failures and signed-out users', () => {
      const handler = TestBed.runInInjectionContext(() => new ReportingErrorHandler());
      const auth = TestBed.inject(AuthService);
      vi.spyOn(console, 'error').mockImplementation(() => undefined);

      handler.handleError(new Error('before sign-in'));
      backend.expectNone('/api/client-errors');

      vi.spyOn(auth, 'token', 'get').mockReturnValue('tok');
      handler.handleError(new HttpErrorResponse({ status: 500 }));
      backend.expectNone('/api/client-errors');

      handler.handleError(new TypeError('x is undefined'));
      handler.handleError(new TypeError('x is undefined'));
      const req = backend.expectOne('/api/client-errors');
      expect(req.request.headers.get('Authorization')).toBe('Bearer tok');
      expect(req.request.body.message).toBe('x is undefined');
      req.flush(null, { status: 204, statusText: 'No Content' });
    });
  });
});
