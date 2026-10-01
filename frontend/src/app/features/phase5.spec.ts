import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { fileNameOf } from '../core/api';
import { AuthService } from '../core/auth.service';
import { MetadataService } from '../core/metadata.service';
import { OfferItem, PublicOffer } from '../core/models';
import { testUser } from '../core/testing';
import { EraseCandidateDialog, PrivacySettings, sameName } from './admin/privacy';
import { SettingsHome } from './admin/settings-home';
import { OfferResponsePage, answerErrors } from './offer/offer-response';
import { OfferDialog, OffersPanel, isOpen, perPeriod } from './recruiting/offers';

const offer = (overrides: Partial<OfferItem> = {}): OfferItem => ({
  id: 'o1', applicationId: 'a1', status: 'Draft', jobTitle: 'Data Analyst', salary: 120000, currency: 'AUD', payPeriod: 'Year',
  startDate: '2026-11-02', expiresAt: '2026-10-08T06:00:00Z', letter: 'Dear {{name}}', notes: null, createdByName: 'Ada Lovelace',
  createdAt: '2026-10-01T00:00:00Z', sentAt: null, sentByName: null, respondedAt: null, signedName: null, responseIp: null, declineReason: null,
  ...overrides,
});

const publicOffer = (overrides: Partial<PublicOffer> = {}): PublicOffer => ({
  organisation: 'Talent Co', candidateName: 'Nina Patel', jobTitle: 'Data Analyst', status: 'Sent', expiresAt: '2026-10-08T06:00:00Z',
  salary: 120000, currency: 'AUD', payPeriod: 'Year', startDate: '2026-11-02', letter: 'Dear Nina Patel,\n\nWelcome aboard.',
  respondedAt: null, signedName: null, ...overrides,
});

describe('Phase 5: offers and candidate privacy', () => {
  let backend: HttpTestingController;
  let dialogData: unknown;
  const close = vi.fn();
  const token = 'a'.repeat(43);

  beforeEach(() => {
    close.mockReset();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: MAT_DIALOG_DATA, useFactory: () => dialogData },
        { provide: MatDialogRef, useValue: { close } },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
    vi.restoreAllMocks();
  });

  function render<T>(component: Type<T>, inputs: Record<string, unknown> = {}): ComponentFixture<T> {
    const fixture = TestBed.createComponent(component);
    for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
    fixture.detectChanges();
    return fixture;
  }

  async function settle(fixture: ComponentFixture<unknown>): Promise<string> {
    await fixture.whenStable();
    fixture.detectChanges();
    return (fixture.nativeElement as HTMLElement).textContent ?? '';
  }

  it('knows open offers, pay periods, signatures and file names', () => {
    expect([isOpen('Draft'), isOpen('Sent'), isOpen('Accepted'), isOpen('Withdrawn')]).toEqual([true, true, false, false]);
    expect(perPeriod('Hour')).toBe('per hour');
    expect(sameName('  nina   PATEL ', 'Nina Patel')).toBe(true);
    expect(sameName('', '')).toBe(false);
    expect(sameName('Nina', 'Nina Patel')).toBe(false);
    expect(fileNameOf('attachment; filename=candidate-data-20261001.zip', 'x.zip')).toBe('candidate-data-20261001.zip');
    expect(fileNameOf("attachment; filename*=UTF-8''a%20b.csv", 'x.csv')).toBe('a b.csv');
    expect(fileNameOf(null, 'x.csv')).toBe('x.csv');
    expect(answerErrors(new HttpErrorResponse({ status: 404 }))[0]).toContain("isn't valid");
    expect(answerErrors(new HttpErrorResponse({ status: 400, error: { errors: { status: ['This offer has expired.'] } } }))).toEqual(['This offer has expired.']);
  });

  it('lists offers with their outcome and only offers a new one when none is open', async () => {
    const fixture = render(OffersPanel, { applicationId: 'a1' });
    backend.expectOne('/api/offers?applicationId=a1').flush([offer({ status: 'Sent', sentAt: '2026-10-01T01:00:00Z', sentByName: 'Ada Lovelace' })]);
    let text = await settle(fixture);
    expect(text).toContain('Data Analyst');
    expect(text).toContain('Sent');
    expect(text).toContain('A$120,000');
    expect(text).not.toContain('Make offer');

    fixture.componentInstance['data'].reload();
    TestBed.tick();
    backend.expectOne('/api/offers?applicationId=a1').flush([
      offer({ id: 'o2', status: 'Accepted', respondedAt: '2026-10-02T01:00:00Z', signedName: 'Nina Patel', responseIp: '203.0.113.9' }),
    ]);
    text = await settle(fixture);
    expect(text).toContain('Make offer');
    expect(text).toContain('signed "Nina Patel" from 203.0.113.9');
  });

  it('drafts an offer from the suggested terms', async () => {
    dialogData = { applicationId: 'a1' };
    const fixture = render(OfferDialog);
    backend.expectOne('/api/offers/defaults?applicationId=a1').flush({
      jobTitle: 'Data Analyst', salary: 115000, currency: 'AUD', payPeriod: 'Year', startDate: '2026-11-02',
      expiresAt: '2026-10-08T06:00:00Z', letter: 'Dear {{name}}, {{offer.salary}}',
    });
    await settle(fixture);
    const dialog = fixture.componentInstance;
    expect(dialog.form.controls.salary.value).toBe(115000);

    dialog.form.patchValue({ currency: 'aud' });
    dialog.save();
    expect(dialog.form.controls.currency.invalid).toBe(true);
    dialog.form.patchValue({ currency: 'NZD', salary: 120000, notes: '  Approved  ' });
    dialog.save();
    const req = backend.expectOne('/api/offers');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      applicationId: 'a1', jobTitle: 'Data Analyst', salary: 120000, currency: 'NZD', payPeriod: 'Year', startDate: '2026-11-02',
      expiresAt: new Date('2026-10-08T06:00:00Z').toISOString(),
      letter: 'Dear {{name}}, {{offer.salary}}', notes: 'Approved',
    });
    req.flush(offer());
    expect(close).toHaveBeenCalledWith(offer());
  });

  it('lets the candidate read the offer and accept it by typing their name', async () => {
    const fixture = render(OfferResponsePage, { token });
    backend.expectOne(`/api/offer-response/${token}`).flush(publicOffer());
    let text = await settle(fixture);
    expect(text).toContain('Job offer: Data Analyst');
    expect(text).toContain('Welcome aboard.');
    expect(text).toContain('Respond by');
    const page = fixture.componentInstance;

    page.accept();
    expect(page.errors()).toEqual(['Type your full name to sign.', 'Tick the box to confirm you accept the offer.']);
    page.signedName = 'Nina Patel';
    page.agree = true;
    page.accept();
    const req = backend.expectOne(`/api/offer-response/${token}/accept`);
    expect(req.request.body).toEqual({ signedName: 'Nina Patel', agree: true });
    req.flush({ status: 'Accepted', message: 'Thank you. Your acceptance has been recorded.' });
    TestBed.tick();
    backend.expectOne(`/api/offer-response/${token}`).flush(publicOffer({ status: 'Accepted', respondedAt: '2026-10-02T01:00:00Z', signedName: 'Nina Patel' }));
    text = await settle(fixture);
    expect(text).toContain('Thank you. Your acceptance has been recorded.');
    expect(text).toContain('You accepted this offer');
    expect(text).not.toContain('Accept offer');
  });

  it('explains expired offers and invalid links, and shows server refusals', async () => {
    const fixture = render(OfferResponsePage, { token });
    backend.expectOne(`/api/offer-response/${token}`).flush(publicOffer({ status: 'Expired', salary: null, letter: null }));
    let text = await settle(fixture);
    expect(text).toContain('This offer expired');
    expect(text).not.toContain('Accept offer');

    const missing = render(OfferResponsePage, { token: 'b'.repeat(43) });
    backend.expectOne(`/api/offer-response/${'b'.repeat(43)}`).flush(null, { status: 404, statusText: 'Not Found' });
    expect(await settle(missing)).toContain("This link isn't valid");

    const open = render(OfferResponsePage, { token: 'c'.repeat(43) });
    backend.expectOne(`/api/offer-response/${'c'.repeat(43)}`).flush(publicOffer());
    await settle(open);
    open.componentInstance.declining.set(true);
    open.componentInstance.decline();
    backend.expectOne(`/api/offer-response/${'c'.repeat(43)}/decline`)
      .flush({ errors: { status: ['This offer has expired.'] } }, { status: 400, statusText: 'Bad Request' });
    text = await settle(open);
    expect(text).toContain('This offer has expired.');
  });

  it('confirms erasure only when the name is typed, then reports what was removed', async () => {
    dialogData = { id: 'c1', name: 'Omar Khan' };
    const fixture = render(EraseCandidateDialog);
    const dialog = fixture.componentInstance;
    expect(dialog.matches()).toBe(false);
    dialog.typed.set(' omar  khan');
    expect(dialog.matches()).toBe(true);
    dialog.erase();
    const req = backend.expectOne('/api/privacy/candidates/c1/erase');
    expect(req.request.body).toEqual({ confirmName: ' omar  khan' });
    req.flush({ applications: 2, activities: 5, files: 1, emails: 3 });
    expect(close).toHaveBeenCalledWith({ applications: 2, activities: 5, files: 1, emails: 3 });
  });

  it('previews the retention policy and saves the chosen period', async () => {
    const fixture = render(PrivacySettings);
    backend.expectOne('/api/privacy/retention').flush({
      months: 24, cutoff: '2024-10-01T00:00:00Z', eligible: 2,
      sample: [{ id: 'c1', name: 'Old Rejected', lastActivity: '2024-01-05T00:00:00Z', applications: 1 }],
    });
    const text = await settle(fixture);
    expect(text).toContain('2 candidates inactive since 1 Oct 2024');
    expect(text).toContain('Old Rejected');
    const page = fixture.componentInstance;
    expect(page.months()).toBe(24);

    page.months.set(null);
    page.save();
    const req = backend.expectOne('/api/privacy/retention');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ months: null });
    req.flush({ months: null, cutoff: null, eligible: 0, sample: [] });
    await settle(fixture);
    expect(page.months()).toBeNull();
  });

  it('shows the Privacy settings area only to recruitment administrators', () => {
    const meta = TestBed.inject(MetadataService);
    TestBed.inject(AuthService).user.set(testUser({ permissions: { settings: { manage: 'All' } } }));
    meta.data.set({ tenant: testUser().tenant, objects: [], picklists: {} });
    expect(render(SettingsHome).componentInstance.areas().map((a) => a.path)).not.toContain('privacy');
    meta.data.set({
      tenant: testUser().tenant, picklists: {},
      objects: [{ key: 'candidate', label: 'Candidate', pluralLabel: 'Candidates', icon: 'person', description: null, nameLabel: 'Full name',
        isStandard: false, isActive: true, hasPipeline: false, sortOrder: 1, fields: [], stages: [] }],
    });
    expect(render(SettingsHome).componentInstance.areas().map((a) => a.path)).toContain('privacy');
  });
});
