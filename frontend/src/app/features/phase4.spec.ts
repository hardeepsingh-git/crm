import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AuthService } from '../core/auth.service';
import { MetadataService } from '../core/metadata.service';
import { HiringAnalytics, InterviewItem, ObjectDef } from '../core/models';
import { NotifyService } from '../core/notify.service';
import { testUser } from '../core/testing';
import { Picklists } from './admin/picklists';
import { HiringAnalyticsPage } from './recruiting/analytics';
import {
  InterviewsPanel, MyInterviewsPage, ScheduleInterviewDialog, ScheduleInterviewData, ScorecardDialog, canGiveFeedback, isUrl,
} from './recruiting/interviews';

const application: ObjectDef = {
  key: 'application', label: 'Application', pluralLabel: 'Applications', icon: 'assignment_ind', description: null, nameLabel: 'Candidate name',
  isStandard: false, isActive: true, hasPipeline: true, sortOrder: 3, fields: [],
  stages: [
    { key: 'applied', label: 'Applied', probability: 0, category: 'Open', color: '#888' },
    { key: 'interview', label: 'Interview', probability: 0, category: 'Open', color: '#36c' },
    { key: 'hired', label: 'Hired', probability: 100, category: 'Won', color: '#2a2' },
    { key: 'rejected', label: 'Rejected', probability: 0, category: 'Lost', color: '#c33' },
  ],
};

const past = '2026-09-28T03:00:00Z';
const interview = (overrides: Partial<InterviewItem> = {}): InterviewItem => ({
  id: 'i1', applicationId: 'a1', applicationName: 'Nina Patel', jobTitle: 'Data Analyst', scheduledAt: past, durationMinutes: 45,
  kind: 'Video', location: 'https://meet.example.com/x', notes: null, status: 'Scheduled', organizerName: 'Ada Lovelace',
  interviewers: [{ id: 'u1', name: 'Ada Lovelace', submitted: false }, { id: 'u2', name: 'Grace Hopper', submitted: true }],
  iAmInterviewer: true, myScorecard: null, scorecards: [], canEdit: true, ...overrides,
});

describe('Phase 4: interviews, scorecards and hiring analytics', () => {
  let backend: HttpTestingController;
  let dialogData: unknown;
  const close = vi.fn();

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
    TestBed.inject(MetadataService).data.set({
      tenant: { ...testUser().tenant, template: 'recruitment' }, objects: [application],
      picklists: { 'interview.criteria': ['Skills', 'Communication'] },
    });
    TestBed.inject(AuthService).user.set(testUser({ permissions: { application: { read: 'All', update: 'All' } } }));
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

  it('offers feedback only to interviewers once the interview has started', () => {
    const now = new Date('2026-10-01T00:00:00Z');
    expect(canGiveFeedback(interview(), now)).toBe(true);
    expect(canGiveFeedback(interview({ scheduledAt: '2026-10-02T00:00:00Z' }), now)).toBe(false);
    expect(canGiveFeedback(interview({ status: 'Cancelled' }), now)).toBe(false);
    expect(canGiveFeedback(interview({ iAmInterviewer: false }), now)).toBe(false);
    expect([isUrl('https://x.test'), isUrl('Room 4'), isUrl(null)]).toEqual([true, false, false]);
  });

  it('lists an application\'s interviews and keeps colleagues\' feedback hidden until mine is in', async () => {
    const fixture = render(InterviewsPanel, { applicationId: 'a1', applicationName: 'Nina Patel', ownerId: 'u9' });
    backend.expectOne('/api/interviews?applicationId=a1').flush([interview()]);
    let text = await settle(fixture);
    expect(text).toContain('Schedule interview');
    expect(text).toContain('Video interview');
    expect(text).toContain('Give feedback');
    expect(text).toContain("Colleagues' feedback is hidden until you submit yours.");
    expect((fixture.nativeElement as HTMLElement).querySelector('a[href="https://meet.example.com/x"]')).not.toBeNull();

    const card = { id: 's1', interviewerId: 'u2', interviewerName: 'Grace Hopper', recommendation: 'StrongYes' as const, ratings: { Skills: 5 }, notes: 'Excellent.', submittedAt: past };
    TestBed.inject(AuthService).user.set(testUser({ permissions: { application: { read: 'All' } } }));
    fixture.componentInstance['data'].reload();
    TestBed.tick();
    backend.expectOne('/api/interviews?applicationId=a1').flush([interview({ iAmInterviewer: false, scorecards: [card] })]);
    text = await settle(fixture);
    expect(text).not.toContain('Schedule interview');
    expect(text).not.toContain('Give feedback');
    expect(text).toContain('Strong yes');
    expect(text).toContain('Skills 5/5');
    expect(text).toContain('Excellent.');
  });

  it('schedules an interview with the chosen interviewers and reports why the invitation was not sent', async () => {
    dialogData = { applicationId: 'a1', applicationName: 'Nina Patel' } satisfies ScheduleInterviewData;
    const fixture = render(ScheduleInterviewDialog);
    backend.expectOne('/api/users/lookup').flush([{ id: 'u1', fullName: 'Ada Lovelace', email: 'a@b.c' }, { id: 'u2', fullName: 'Grace Hopper', email: 'g@b.c' }]);
    await settle(fixture);
    const dialog = fixture.componentInstance;
    expect(dialog.form.controls.interviewerIds.value).toEqual(['u1']);

    dialog.save();
    expect(dialog.form.controls.scheduledAt.hasError('required')).toBe(true);
    dialog.form.patchValue({ scheduledAt: '2026-10-08T14:30', interviewerIds: ['u1', 'u2'], location: ' https://meet.example.com/y ', emailCandidate: true });
    dialog.save();
    const req = backend.expectOne('/api/interviews');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      applicationId: 'a1', scheduledAt: new Date('2026-10-08T14:30').toISOString(), durationMinutes: 60, kind: 'Video',
      location: 'https://meet.example.com/y', notes: null, interviewerIds: ['u1', 'u2'], emailCandidate: true,
    });
    const warn = vi.spyOn(TestBed.inject(NotifyService), 'warn').mockImplementation(() => undefined);
    req.flush({ interview: interview(), emailNote: 'The candidate has no email address.' }, { status: 201, statusText: 'Created' });
    expect(warn).toHaveBeenCalledWith('Interview saved. The candidate has no email address.');
    expect(close).toHaveBeenCalledWith(interview());
  });

  it('reschedules with PUT, keeping the current interviewers', async () => {
    dialogData = { applicationId: 'a1', applicationName: 'Nina Patel', item: interview() } satisfies ScheduleInterviewData;
    const fixture = render(ScheduleInterviewDialog);
    backend.expectOne('/api/users/lookup').flush([]);
    await settle(fixture);
    fixture.componentInstance.save();
    const req = backend.expectOne('/api/interviews/i1');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body.interviewerIds).toEqual(['u1', 'u2']);
    expect(req.request.body.durationMinutes).toBe(45);
    req.flush({ interview: interview(), emailNote: null });
  });

  it('submits a scorecard against the current criteria', async () => {
    dialogData = interview({ myScorecard: { id: 's1', interviewerId: 'u1', interviewerName: 'Ada', recommendation: 'Yes', ratings: { Skills: 3, Retired: 2 }, notes: 'Ok', submittedAt: past } });
    const fixture = render(ScorecardDialog);
    const text = await settle(fixture);
    expect(text).toContain('Skills');
    expect(text).toContain('Communication');
    const dialog = fixture.componentInstance;
    expect(dialog.ratings()).toEqual({ Skills: 3 });

    dialog.rate('Communication', 5);
    dialog.recommendation.set('StrongYes');
    dialog.notes = ' Great communicator. ';
    dialog.save();
    const req = backend.expectOne('/api/interviews/i1/scorecard');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ recommendation: 'StrongYes', ratings: { Skills: 3, Communication: 5 }, notes: 'Great communicator.' });
    req.flush(interview());
    expect(close).toHaveBeenCalled();
  });

  it('shows my upcoming interviews and the ones waiting for my feedback', async () => {
    const fixture = render(MyInterviewsPage);
    backend.expectOne('/api/interviews/mine').flush({
      upcoming: [interview({ id: 'i2', applicationName: 'Omar Khan', scheduledAt: '2026-10-09T01:00:00Z', location: 'Room 4' })],
      awaitingFeedback: [interview()],
    });
    const text = await settle(fixture);
    expect(text).toContain('Feedback needed (1)');
    expect(text).toContain('Upcoming (1)');
    expect(text).toContain('Omar Khan');
    expect(text).toContain('Room 4');
  });

  it('charts the hiring funnel and refetches when filters change', async () => {
    const report: HiringAnalytics = {
      days: 90, applications: 4, open: 2, hired: 1, rejected: 1, averageDaysToHire: 12.5, medianDaysToHire: 12.5,
      funnel: [
        { key: 'applied', label: 'Applied', color: '#888', current: 1, reached: 4, conversionToNext: 0.75, averageDaysInStage: 2 },
        { key: 'interview', label: 'Interview', color: '#36c', current: 1, reached: 3, conversionToNext: 0.333, averageDaysInStage: 5.5 },
        { key: 'hired', label: 'Hired', color: '#2a2', current: 1, reached: 1, conversionToNext: null, averageDaysInStage: null },
        { key: 'rejected', label: 'Rejected', color: '#c33', current: 1, reached: 1, conversionToNext: null, averageDaysInStage: null },
      ],
      sources: [{ source: 'Referral', applications: 2, hired: 1, hireRate: 0.5 }],
      jobs: [{ id: 'j1', title: 'Data Analyst', applications: 4, open: 2, hired: 1, byStage: { applied: 1, interview: 1, hired: 1, rejected: 1 } }],
      interviewsNext7Days: 2, feedbackOutstanding: 3,
    };
    const fixture = render(HiringAnalyticsPage);
    backend.expectOne((r) => r.url === '/api/records/position').flush({ items: [{ id: 'j1', name: 'Data Analyst' }], total: 1, page: 1, pageSize: 100 });
    backend.expectOne('/api/recruiting/analytics?days=90').flush(report);
    const text = await settle(fixture);
    expect(text).toContain('12.5 d');
    expect(text).toContain('75%');
    expect(text).toContain('Hired: 1');
    expect(text).toContain('Referral');
    expect(text).toContain('50%');
    expect(fixture.componentInstance.openStages().map((s) => s.key)).toEqual(['applied', 'interview']);
    expect(fixture.componentInstance.share(3)).toBe(75);

    fixture.componentInstance.days.set(30);
    fixture.componentInstance.positionId.set('j1');
    TestBed.tick();
    backend.expectOne('/api/recruiting/analytics?days=30&positionId=j1').flush(report);
  });

  it('lets admins edit interview criteria only when recruitment is set up', () => {
    expect(render(Picklists).componentInstance.picklists.map((p) => p.key)).toContain('interview.criteria');
    TestBed.inject(MetadataService).data.set({ tenant: testUser().tenant, objects: [], picklists: {} });
    expect(render(Picklists).componentInstance.picklists.map((p) => p.key)).not.toContain('interview.criteria');
  });
});
