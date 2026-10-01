import { HttpErrorResponse, HttpParams, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AuthService } from '../core/auth.service';
import { MetadataService } from '../core/metadata.service';
import { CareerJob, OrganisationSettings } from '../core/models';
import { NotifyService } from '../core/notify.service';
import { testUser } from '../core/testing';
import { ExportButton } from '../shared/export-button';
import { GeneralSettings } from './admin/general-settings';
import { CareerJobView, CareersPageView, jobFacts, problemMessages } from './careers/careers';

const job: CareerJob = {
  id: 'j1', title: 'Data Analyst', department: 'Analytics', location: 'Remote', employmentType: 'Contract',
  closingDate: '2026-12-01', postedAt: '2026-10-01T00:00:00Z',
};

describe('Phase 3: careers page and CSV export', () => {
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
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

  it('summarises jobs and turns API problems into applicant-friendly messages', () => {
    expect(jobFacts(job)).toBe('Analytics · Remote · Contract');
    expect(jobFacts({ ...job, department: null, employmentType: null })).toBe('Remote');
    expect(problemMessages(new HttpErrorResponse({ status: 404 }))).toEqual(['Sorry, this job is no longer open.']);
    expect(problemMessages(new HttpErrorResponse({ status: 400, error: { errors: { cv: ['Attach your CV.'], consent: ['Agree'] } } })))
      .toEqual(['Attach your CV.', 'Agree']);
  });

  it('lists the published jobs with links to apply', async () => {
    const fixture = render(CareersPageView, { slug: 'talent-co' });
    backend.expectOne('/api/careers/talent-co').flush({ organisation: 'Talent Co', intro: 'Join us.', jobs: [job] });
    const text = await settle(fixture);
    expect(text).toContain('Careers at Talent Co');
    expect(text).toContain('Join us.');
    expect(text).toContain('Open positions (1)');
    expect((fixture.nativeElement as HTMLElement).querySelector('a.job')?.getAttribute('href')).toBe('/j1');
  });

  it('says so when a careers page is not available', async () => {
    const fixture = render(CareersPageView, { slug: 'nope' });
    backend.expectOne('/api/careers/nope').flush(null, { status: 404, statusText: 'Not Found' });
    expect(await settle(fixture)).toContain("This careers page isn't available");
  });

  it('checks the application in the browser, then sends it with the CV and shows the thanks', async () => {
    const fixture = render(CareerJobView, { slug: 'talent-co', jobId: 'j1' });
    backend.expectOne('/api/careers/talent-co/jobs/j1').flush({ organisation: 'Talent Co', job, description: 'Great role.' });
    expect(await settle(fixture)).toContain('Great role.');
    const view = fixture.componentInstance;

    view.form.patchValue({ name: 'Nina Patel', email: 'nina@example.com', linkedin: 'linkedin.com/in/nina' });
    view.submit();
    expect(view.errors()).toEqual([
      'Enter your LinkedIn profile as a full link, starting with https://.', 'Attach your CV.', 'Please agree to the privacy notice to apply.',
    ]);

    view.pick({ 0: new File(['x'.repeat(10)], 'cv.pdf'), length: 1, item: () => null } as unknown as FileList);
    view.form.patchValue({ linkedin: 'https://linkedin.com/in/nina', consent: true });
    view.submit();
    const req = backend.expectOne('/api/careers/talent-co/jobs/j1/apply');
    const body = req.request.body as FormData;
    expect([body.get('name'), body.get('email'), body.get('consent'), body.get('website')]).toEqual(['Nina Patel', 'nina@example.com', 'true', '']);
    expect((body.get('cv') as File).name).toBe('cv.pdf');
    req.flush({ message: 'Thank you for applying.' }, { status: 202, statusText: 'Accepted' });
    expect(await settle(fixture)).toContain('Application sent');
  });

  it('rejects oversized CVs before upload and shows server errors', async () => {
    const fixture = render(CareerJobView, { slug: 'talent-co', jobId: 'j1' });
    backend.expectOne('/api/careers/talent-co/jobs/j1').flush({ organisation: 'Talent Co', job, description: null });
    await settle(fixture);
    const view = fixture.componentInstance;
    const big = new File(['x'], 'cv.pdf');
    Object.defineProperty(big, 'size', { value: 6 * 1024 * 1024 });
    view.pick({ 0: big, length: 1, item: () => null } as unknown as FileList);
    expect(view.cv()).toBeNull();
    expect(view.errors()).toEqual(['Your CV can be at most 5 MB.']);

    view.pick({ 0: new File(['%PDF'], 'cv.pdf'), length: 1, item: () => null } as unknown as FileList);
    view.form.patchValue({ name: 'A', email: 'a@example.com', consent: true });
    view.submit();
    backend.expectOne('/api/careers/talent-co/jobs/j1/apply')
      .flush({ errors: { cv: ["This file doesn't look like the document type its name says."] } }, { status: 400, statusText: 'Bad Request' });
    expect(await settle(fixture)).toContain("doesn't look like the document type");
  });

  it('exports the filtered list as a CSV download', async () => {
    const created = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:x');
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    const params = new HttpParams().set('cf.position', 'p1').set('page', '2').set('pageSize', '20').set('search', 'nina');
    const fixture = render(ExportButton, { objectKey: 'application', params });

    (fixture.nativeElement as HTMLElement).querySelector('button')!.click();
    const req = backend.expectOne((r) => r.url === '/api/export/application');
    expect(req.request.params.keys().sort()).toEqual(['cf.position', 'search']);
    req.flush(new Blob(['Id\r\n']), { headers: { 'Content-Disposition': 'attachment; filename=applications-20261001.csv' } });
    expect(created).toHaveBeenCalled();
    expect(click).toHaveBeenCalled();
    expect(fixture.componentInstance.busy()).toBe(false);

    const warn = vi.spyOn(TestBed.inject(NotifyService), 'warn').mockImplementation(() => undefined);
    (fixture.nativeElement as HTMLElement).querySelector('button')!.click();
    backend.expectOne((r) => r.url === '/api/export/application')
      .flush(new Blob([JSON.stringify({ errors: { filters: ['12000 records match, and an export can hold at most 10000.'] } })]),
        { status: 400, statusText: 'Bad Request' });
    await vi.waitFor(() => expect(warn).toHaveBeenCalledWith('12000 records match, and an export can hold at most 10000.'));
  });

  it('lets recruitment organisations publish their careers page', async () => {
    TestBed.inject(MetadataService).data.set({ tenant: { ...testUser().tenant, slug: 'talent-co' }, objects: [], picklists: {} });
    TestBed.inject(AuthService).user.set(testUser());
    const fixture = render(GeneralSettings);
    const settings: OrganisationSettings = {
      id: 't1', name: 'Acme', slug: 'talent-co', currency: 'AUD', template: 'recruitment', selfRegistrationEnabled: false,
      selfRegistrationAllowedByPlatform: false, emailVerification: false, smsVerification: false,
      careersEnabled: false, careersIntro: null, careersAvailable: true,
    };
    backend.expectOne('/api/settings').flush(settings);
    expect(await settle(fixture)).toContain('Publish a public careers page');

    const page = fixture.componentInstance;
    page.careers.set(true);
    page.careersIntro.set('We hire kind people.');
    expect(await settle(fixture)).toContain('/careers/talent-co');
    page.save();
    const save = backend.expectOne('/api/settings');
    expect(save.request.body).toMatchObject({ careersEnabled: true, careersIntro: 'We hire kind people.' });
    save.flush({ ...settings, careersEnabled: true, careersIntro: 'We hire kind people.' });
    for (const req of backend.match('/api/metadata')) req.flush({ tenant: testUser().tenant, objects: [], picklists: {} });
    for (const req of backend.match(() => true)) req.flush({});
  });
});
