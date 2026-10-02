import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AuthService } from '../core/auth.service';
import { MetadataService } from '../core/metadata.service';
import { MockInterviewItem, PublicMockInterview } from '../core/models';
import { testUser } from '../core/testing';
import { ThemeService, resolveTheme, storedTheme } from '../core/theme.service';
import { MockInterviewSettings } from './admin/mock-settings';
import { SettingsHome } from './admin/settings-home';
import { MockSessionPage, clock, isDirty } from './mock/mock-session';
import { MockInterviewsPanel, MockInviteDialog, MockReviewDialog, isMockOpen, mockStatusLabel, mockTone, scoreBand } from './recruiting/mock-interviews';

const mock = (overrides: Partial<MockInterviewItem> = {}): MockInterviewItem => ({
  id: 'm1', applicationId: 'a1', status: 'Sent', jobTitle: 'Data Analyst', message: null, sentAt: '2026-10-01T00:00:00Z', sentByName: 'Ada Lovelace',
  expiresAt: '2026-10-08T00:00:00Z', startedAt: null, submittedAt: null, questionCount: 3, answeredCount: 0, averageSelfRating: null, minutesTaken: null,
  questions: [], readinessRating: null, reviewNotes: null, reviewedByName: null, reviewedAt: null, aiStatus: 'NotRequested', aiScore: null,
  aiSummary: null, aiStrengths: [], aiImprovements: [], aiModel: null, aiReviewedAt: null, aiError: null, ...overrides,
});

const session = (overrides: Partial<PublicMockInterview> = {}): PublicMockInterview => ({
  organisation: 'Talent Co', candidateName: 'Nina Patel', jobTitle: 'Data Analyst', message: 'No trick questions.', status: 'Sent',
  expiresAt: '2026-10-08T00:00:00Z', submittedAt: null,
  questions: [
    { id: 'q1', prompt: 'Tell us about yourself.', details: null, competency: 'Communication', minutes: 3, answer: null, rating: null, guidance: null },
    { id: 'q2', prompt: 'Describe a hard problem.', details: null, competency: 'Problem solving', minutes: 4, answer: null, rating: null, guidance: null },
  ],
  ...overrides,
});

describe('Phase 6: mock interviews and themes', () => {
  let backend: HttpTestingController;
  let dialogData: unknown;
  const close = vi.fn();
  const token = 'b'.repeat(43);

  beforeEach(() => {
    close.mockReset();
    localStorage.removeItem('crm.theme');
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
    document.documentElement.classList.remove('dark-theme');
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

  it('labels statuses, scores and timers, and resolves themes', () => {
    expect([isMockOpen('Sent'), isMockOpen('InProgress'), isMockOpen('Submitted'), isMockOpen('Withdrawn')]).toEqual([true, true, false, false]);
    expect(mockStatusLabel('Submitted')).toBe('Completed');
    expect([mockTone('Submitted'), mockTone('Sent'), mockTone('Expired')]).toEqual(['success', 'info', 'neutral']);
    expect([scoreBand(4.6), scoreBand(3.5), scoreBand(2.5), scoreBand(1), scoreBand(null)]).toEqual(['Excellent', 'Strong', 'Developing', 'Needs work', '']);
    expect([clock(0), clock(65), clock(600)]).toEqual(['0:00', '1:05', '10:00']);
    expect(isDirty({ text: ' a ', rating: 3, guidance: null, saved: { text: 'a', rating: 3 } })).toBe(false);
    expect(isDirty({ text: 'a', rating: 4, guidance: null, saved: { text: 'a', rating: 3 } })).toBe(true);
    expect([resolveTheme('Dark', false), resolveTheme('Light', true), resolveTheme('System', true), resolveTheme('System', false)]).toEqual(['dark', 'light', 'dark', 'light']);
    expect(storedTheme({ getItem: () => 'Dark' })).toBe('Dark');
    expect(storedTheme({ getItem: () => 'Purple' })).toBe('System');
  });

  it('follows the signed-in user\'s theme and saves a new choice to their profile', () => {
    const theme = TestBed.inject(ThemeService);
    TestBed.tick();
    expect(document.documentElement.classList.contains('dark-theme')).toBe(false);

    TestBed.inject(AuthService).user.set(testUser({ theme: 'Dark' }));
    TestBed.tick();
    expect(theme.preference()).toBe('Dark');
    expect(document.documentElement.classList.contains('dark-theme')).toBe(true);

    theme.set('Light');
    TestBed.tick();
    const req = backend.expectOne('/api/auth/me/preferences');
    expect([req.request.method, req.request.body]).toEqual(['PUT', { theme: 'Light' }]);
    req.flush(null);
    expect(document.documentElement.classList.contains('dark-theme')).toBe(false);
    expect(localStorage.getItem('crm.theme')).toBe('Light');
    expect(TestBed.inject(AuthService).user()?.theme).toBe('Light');
  });

  it('shows mock interviews with status, scores and actions', async () => {
    const fixture = render(MockInterviewsPanel, { applicationId: 'a1' });
    backend.expectOne('/api/mock-interviews?applicationId=a1').flush({
      items: [mock({ status: 'Submitted', answeredCount: 2, submittedAt: '2026-10-02T00:00:00Z', aiStatus: 'Completed', aiScore: 3.4, readinessRating: 4 })],
      aiAvailable: false, aiUnavailableReason: 'AI feedback is switched off on this server.',
    });
    const text = await settle(fixture);
    expect(text).toContain('Completed');
    expect(text).toContain('3.4');
    expect(text).toContain('Readiness 4/5');
    expect(text).toContain('Review');
    expect(text).toContain('Invite to practise');
  });

  it('invites with the first questions pre-selected, keeping library order', async () => {
    dialogData = { applicationId: 'a1' };
    const fixture = render(MockInviteDialog);
    backend.expectOne('/api/mock-interviews/library?applicationId=a1').flush({
      available: true, canAdd: true,
      questions: Array.from({ length: 7 }, (_, i) => ({ id: `q${i}`, prompt: `Question ${i}`, competency: null, minutes: 2, forThisJob: i === 0 })),
    });
    const text = await settle(fixture);
    const dialog = fixture.componentInstance;
    expect(dialog.chosen().size).toBe(5);
    expect(text).toContain('about 10 min');
    expect(text).toContain('This job');

    dialog.toggle('q6');
    dialog.toggle('q0');
    dialog.message = '  Good luck  ';
    dialog.send();
    const req = backend.expectOne('/api/mock-interviews');
    expect(req.request.body).toEqual({ applicationId: 'a1', questionIds: ['q1', 'q2', 'q3', 'q4', 'q6'], validDays: 7, message: 'Good luck' });
    req.flush({ interview: mock(), link: 'https://crm.test/mock/x', emailNote: null });
    expect(close).toHaveBeenCalled();
  });

  it('offers starter questions when the library is empty', async () => {
    dialogData = { applicationId: 'a1' };
    const fixture = render(MockInviteDialog);
    backend.expectOne('/api/mock-interviews/library?applicationId=a1').flush({ available: true, canAdd: true, questions: [] });
    expect(await settle(fixture)).toContain('Add 10 starter questions');
    fixture.componentInstance.addStarter();
    backend.expectOne('/api/mock-interviews/library/starter').flush({ added: 10 });
    TestBed.tick();
    backend.expectOne('/api/mock-interviews/library?applicationId=a1').flush({ available: true, canAdd: true, questions: [] });
  });

  it('lets the hiring team review answers and ask for AI feedback', async () => {
    dialogData = {
      item: mock({
        status: 'Submitted', answeredCount: 1, questions: [
          { question: { id: 'q1', prompt: 'Why us?', details: null, competency: 'Motivation', guidance: 'Be specific', minutes: 2 },
            answer: { text: 'Your mission.', rating: 4, seconds: 90, answeredAt: '2026-10-02T00:00:00Z' }, ai: { id: 'q1', score: 3, feedback: 'Add an example.' } },
        ],
      }),
      aiAvailable: true, aiUnavailableReason: null,
    };
    const fixture = render(MockReviewDialog);
    const text = await settle(fixture);
    expect(text).toContain('Your mission.');
    expect(text).toContain('Add an example.');
    const dialog = fixture.componentInstance;

    dialog.askAi();
    backend.expectOne('/api/mock-interviews/m1/ai-review').flush(mock({ status: 'Submitted', aiStatus: 'Pending' }));
    expect(dialog.item().aiStatus).toBe('Pending');

    dialog.rating = 4;
    dialog.notes = ' Ready ';
    dialog.save();
    const req = backend.expectOne('/api/mock-interviews/m1/review');
    expect(req.request.body).toEqual({ readinessRating: 4, notes: 'Ready' });
    req.flush(mock({ status: 'Submitted', readinessRating: 4, reviewNotes: 'Ready' }));
    expect(dialog.changed).toBe(true);
  });

  it('walks the candidate through questions, saving answers and revealing tips', async () => {
    const fixture = render(MockSessionPage, { token });
    backend.expectOne(`/api/mock-response/${token}`).flush(session());
    let text = await settle(fixture);
    expect(text).toContain('Hi Nina');
    expect(text).toContain('No trick questions.');
    expect(text).toContain('About 7 minutes');

    const page = fixture.componentInstance;
    page.go(page.firstUnanswered());
    text = await settle(fixture);
    expect(text).toContain('Question 1 of 2');
    page.edit('q1', { text: 'I build APIs.', rating: 4 });
    page.leave(session().questions[0], null);
    const save = backend.expectOne(`/api/mock-response/${token}/answers/q1`);
    expect(save.request.method).toBe('PUT');
    expect(save.request.body).toMatchObject({ text: 'I build APIs.', rating: 4 });
    save.flush({ guidance: 'Mention impact.', answered: 1, total: 2 });
    text = await settle(fixture);
    expect(text).toContain('Mention impact.');
    expect(page.answered()).toBe(1);

    page.leave(session().questions[0], 1);
    expect(page.step()).toBe(1);
    page.leave(session().questions[1], 2);
    text = await settle(fixture);
    expect(text).toContain('Ready to submit?');
    page.submit();
    backend.expectOne(`/api/mock-response/${token}/submit`).flush({ message: 'Thank you.' });
    TestBed.tick();
    backend.expectOne(`/api/mock-response/${token}`).flush(session({ status: 'Submitted', questions: [
      { ...session().questions[0], answer: 'I build APIs.', guidance: 'Mention impact.' }, session().questions[1],
    ] }));
    text = await settle(fixture);
    expect(text).toContain('Thank you.');
    expect(text).toContain('You skipped this question.');
  });

  it('hides expired sessions and shows the settings card and AI opt-in', async () => {
    const fixture = render(MockSessionPage, { token });
    backend.expectOne(`/api/mock-response/${token}`).flush(session({ status: 'Expired', questions: [], message: null }));
    expect(await settle(fixture)).toContain('no longer available');

    TestBed.inject(AuthService).user.set(testUser({ permissions: { settings: { manage: 'All' } } }));
    vi.spyOn(TestBed.inject(MetadataService), 'isActive').mockImplementation((key: string) => key === 'application');
    expect(await settle(render(SettingsHome))).toContain('Mock interviews');

    const settings = render(MockInterviewSettings);
    backend.expectOne('/api/mock-interviews/settings').flush({
      aiReviewEnabled: false, aiTrigger: 'Manual', aiInstructions: null, aiConnectionReady: false,
      aiConnectionProblem: 'AI feedback is switched off on this server.', libraryAvailable: true,
    });
    expect(await settle(settings)).toContain('switched off on this server');
    settings.componentInstance.patch({ aiReviewEnabled: true, aiTrigger: 'OnSubmit', aiInstructions: '  Be kind  ' });
    settings.componentInstance.save();
    const req = backend.expectOne('/api/mock-interviews/settings');
    expect(req.request.body).toEqual({ aiReviewEnabled: true, aiTrigger: 'OnSubmit', aiInstructions: 'Be kind' });
    req.flush({ aiReviewEnabled: true, aiTrigger: 'OnSubmit', aiInstructions: 'Be kind', aiConnectionReady: false, aiConnectionProblem: null, libraryAvailable: true });
  });
});
