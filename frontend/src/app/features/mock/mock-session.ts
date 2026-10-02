import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, input, linkedSignal, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { interval, map } from 'rxjs';
import { CrmApi } from '../../core/api';
import { PublicMockInterview, PublicMockQuestion } from '../../core/models';
import { ThemeToggle } from '../../shared/theme-toggle';
import { answerErrors } from '../offer/offer-response';

interface Draft {
  text: string;
  rating: number | null;
  guidance: string | null;
  /** What the server last stored, to know whether there is anything to save. */
  saved: { text: string; rating: number | null };
}

export function isDirty(d: Draft): boolean {
  return d.text.trim() !== d.saved.text || d.rating !== d.saved.rating;
}

/** "3:05" for a number of seconds. */
export function clock(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

/** The candidate's practice interview (`/mock/:token`): one question at a time, tips after each answer, then submit. */
@Component({
  selector: 'app-mock-session',
  imports: [DatePipe, FormsModule, MatButtonModule, MatButtonToggleModule, MatCardModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, ThemeToggle],
  template: `
    <div class="page">
      <div class="top no-print"><span class="spacer"></span><app-theme-toggle /></div>
      <main class="mock">
        @if (session.isLoading() && !session.hasValue()) { <mat-progress-bar mode="indeterminate" /> }
        @if (session.error()) {
          <h1>Practice interview</h1>
          <p class="muted">This link isn't valid. It may have been withdrawn or replaced by a newer email. Please contact the person who sent it.</p>
        }
        @if (session.hasValue()) {
          @let s = session.value();
          <header>
            <div class="muted">{{ s.organisation }}</div>
            <h1>Practice interview: {{ s.jobTitle }}</h1>
          </header>

          @if (s.status === 'Expired' || s.status === 'Withdrawn' || s.questions.length === 0) {
            <mat-card appearance="outlined"><mat-card-content>
              <p>This practice interview is no longer available. It expired on {{ s.expiresAt | date: 'd MMMM y, h:mm a' }}. Please contact the team if you'd like a new link.</p>
            </mat-card-content></mat-card>
          } @else if (s.status === 'Submitted') {
            <div class="done" role="status"><mat-icon>celebration</mat-icon>
              <div>
                <strong>{{ submittedMessage() ?? 'Thank you, your answers have been sent to the team.' }}</strong>
                <div class="muted">Here's a recap with tips for each question. This page is available until {{ s.expiresAt | date: 'd MMMM y' }}.</div>
              </div>
            </div>
            @for (q of s.questions; track q.id; let i = $index) {
              <mat-card appearance="outlined" class="recap"><mat-card-content>
                <div class="q-title"><span class="n">{{ i + 1 }}</span> {{ q.prompt }}</div>
                @if (q.answer) { <div class="answer">{{ q.answer }}</div> } @else { <div class="muted">You skipped this question.</div> }
                @if (q.guidance) { <div class="tips"><mat-icon inline>lightbulb</mat-icon> {{ q.guidance }}</div> }
              </mat-card-content></mat-card>
            }
            <button mat-stroked-button class="no-print" (click)="print()"><mat-icon>print</mat-icon> Print or save as PDF</button>
          } @else if (step() < 0) {
            <mat-card appearance="outlined" class="intro"><mat-card-content>
              <p>Hi {{ firstName() }},</p>
              @if (s.message) { <blockquote>{{ s.message }}</blockquote> }
              <ul class="facts">
                <li><mat-icon>quiz</mat-icon> {{ s.questions.length }} questions</li>
                <li><mat-icon>schedule</mat-icon> About {{ totalMinutes() }} minutes</li>
                <li><mat-icon>event</mat-icon> Available until {{ s.expiresAt | date: 'd MMMM y, h:mm a' }}</li>
              </ul>
              <p>Answer in writing as you would out loud. After each answer you'll see what a strong answer covers. You can stop and come back: answers are saved as you go.
                When you submit, your answers are shared with the hiring team to help them prepare.</p>
              <button mat-flat-button (click)="go(firstUnanswered())">{{ answered() ? 'Continue' : 'Start practising' }} <mat-icon iconPositionEnd>arrow_forward</mat-icon></button>
            </mat-card-content></mat-card>
          } @else if (step() < s.questions.length) {
            @let q = s.questions[step()];
            @let d = drafts()[q.id];
            <div class="progress">
              <span>Question {{ step() + 1 }} of {{ s.questions.length }}</span>
              <span class="spacer"></span>
              <span class="timer" [class.over]="q.minutes && elapsed() > q.minutes * 60" aria-label="Time on this question">
                <mat-icon inline>timer</mat-icon> {{ time(elapsed()) }}@if (q.minutes) { / {{ q.minutes }}:00 }
              </span>
            </div>
            <mat-progress-bar mode="determinate" [value]="(step() / s.questions.length) * 100" />
            <nav class="dots" aria-label="Questions">
              @for (x of s.questions; track x.id; let i = $index) {
                <button type="button" class="dot" [class.current]="i === step()" [class.answered]="!!drafts()[x.id].saved.text"
                  (click)="leave(q, i)" [attr.aria-label]="'Question ' + (i + 1)" [attr.aria-current]="i === step() ? 'step' : null">{{ i + 1 }}</button>
              }
            </nav>
            <mat-card appearance="outlined" class="question"><mat-card-content>
              @if (q.competency) { <div class="competency">{{ q.competency }}</div> }
              <h2>{{ q.prompt }}</h2>
              @if (q.details) { <p class="muted">{{ q.details }}</p> }
              <mat-form-field appearance="outline" class="full">
                <mat-label>Your answer</mat-label>
                <textarea matInput rows="8" [ngModel]="d.text" (ngModelChange)="edit(q.id, { text: $event })" maxlength="5000"></textarea>
                <mat-hint align="end">{{ d.text.length }} / 5000</mat-hint>
              </mat-form-field>
              <div class="confidence">
                <span id="conf-label">How confident do you feel?</span>
                <mat-button-toggle-group [ngModel]="d.rating" (ngModelChange)="edit(q.id, { rating: $event })" aria-labelledby="conf-label" hideSingleSelectionIndicator>
                  @for (n of [1, 2, 3, 4, 5]; track n) { <mat-button-toggle [value]="n">{{ n }}</mat-button-toggle> }
                </mat-button-toggle-group>
              </div>
              @for (e of errors(); track e) { <p class="error" role="alert">{{ e }}</p> }
              @if (d.guidance) {
                <div class="tips" role="note"><mat-icon>lightbulb</mat-icon><div><strong>What a strong answer covers</strong><div>{{ d.guidance }}</div></div></div>
              }
              <div class="actions">
                <button mat-button (click)="leave(q, step() - 1)" [disabled]="step() === 0 || busy()"><mat-icon>arrow_back</mat-icon> Back</button>
                <span class="spacer"></span>
                <button mat-stroked-button (click)="leave(q, null)" [disabled]="busy() || !d.text.trim() || (!dirty(d) && !!d.guidance)">Save &amp; see tips</button>
                <button mat-flat-button (click)="leave(q, step() + 1)" [disabled]="busy()">{{ step() === s.questions.length - 1 ? 'Review answers' : 'Next' }} <mat-icon iconPositionEnd>arrow_forward</mat-icon></button>
              </div>
            </mat-card-content></mat-card>
          } @else {
            <mat-card appearance="outlined"><mat-card-content>
              <h2>Ready to submit?</h2>
              <p>You've answered {{ answered() }} of {{ s.questions.length }} questions. You can go back to any question before you submit.</p>
              <ol class="review-list">
                @for (q of s.questions; track q.id; let i = $index) {
                  <li><button mat-button (click)="go(i)">{{ q.prompt }}</button>
                    @if (drafts()[q.id].text.trim()) { <mat-icon class="ok" inline aria-label="Answered">check_circle</mat-icon> } @else { <span class="muted">skipped</span> }
                  </li>
                }
              </ol>
              @for (e of errors(); track e) { <p class="error" role="alert">{{ e }}</p> }
              <div class="actions">
                <button mat-button (click)="go(s.questions.length - 1)"><mat-icon>arrow_back</mat-icon> Back</button>
                <span class="spacer"></span>
                <button mat-flat-button (click)="submit()" [disabled]="busy() || answered() === 0">Submit answers</button>
              </div>
            </mat-card-content></mat-card>
          }
        }
      </main>
    </div>
  `,
  styles: `
    .page { min-height: 100vh; background: var(--mat-sys-surface-container-lowest); }
    .top { display: flex; padding: 8px 16px; }
    .mock { max-width: 780px; margin: 0 auto; padding: 8px 16px 64px; }
    header { margin-bottom: 20px; }
    h1 { margin: 4px 0; }
    h2 { font: var(--mat-sys-title-large); margin: 4px 0 12px; }
    blockquote { margin: 12px 0; padding: 8px 16px; border-left: 4px solid var(--mat-sys-primary); background: var(--mat-sys-surface-container); border-radius: 0 8px 8px 0; }
    .facts { list-style: none; padding: 0; display: flex; flex-wrap: wrap; gap: 16px; }
    .facts li { display: flex; align-items: center; gap: 6px; }
    .facts mat-icon { color: var(--mat-sys-primary); }
    .progress { display: flex; align-items: center; margin-bottom: 8px; font-weight: 500; }
    .timer { display: inline-flex; align-items: center; gap: 4px; font-variant-numeric: tabular-nums; }
    .timer.over { color: var(--crm-warning); }
    .dots { display: flex; flex-wrap: wrap; gap: 6px; margin: 12px 0; }
    .dot { width: 32px; height: 32px; border-radius: 16px; border: 1px solid var(--mat-sys-outline); background: transparent; color: var(--mat-sys-on-surface); cursor: pointer; font: inherit; }
    .dot.answered { background: var(--mat-sys-secondary-container); color: var(--mat-sys-on-secondary-container); border-color: transparent; }
    .dot.current { outline: 2px solid var(--mat-sys-primary); outline-offset: 2px; }
    .dot:focus-visible { outline: 3px solid var(--mat-sys-primary); }
    .competency { font-size: 12px; text-transform: uppercase; letter-spacing: 0.06em; color: var(--mat-sys-primary); font-weight: 600; }
    .full { width: 100%; }
    .confidence { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin: 4px 0 12px; }
    .tips { display: flex; gap: 10px; padding: 12px 16px; border-radius: 12px; background: var(--crm-ai-surface); margin: 8px 0; }
    .tips mat-icon { color: var(--crm-warning); }
    .actions { display: flex; gap: 8px; margin-top: 16px; align-items: center; flex-wrap: wrap; }
    .error { color: var(--mat-sys-error); margin: 8px 0 0; }
    .done { display: flex; gap: 12px; align-items: flex-start; padding: 16px; border-radius: 12px; background: var(--crm-success-container); color: var(--crm-on-success-container); margin-bottom: 16px; }
    .recap { margin-bottom: 12px; }
    .q-title { font-weight: 600; display: flex; gap: 8px; }
    .n { display: inline-grid; place-items: center; min-width: 24px; height: 24px; border-radius: 12px; font-size: 12px; background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container); }
    .answer { white-space: pre-wrap; margin: 8px 0; }
    .review-list li { display: flex; align-items: center; gap: 8px; }
    .review-list button { text-align: left; white-space: normal; height: auto; }
    .ok { color: var(--crm-success); }
    @media print { .no-print { display: none !important; } }
  `,
})
export class MockSessionPage {
  readonly token = input.required<string>();
  private readonly api = inject(CrmApi);
  readonly session = httpResource<PublicMockInterview>(() => `/api/mock-response/${encodeURIComponent(this.token())}`);
  readonly step = signal(-1);
  readonly busy = signal(false);
  readonly errors = signal<string[]>([]);
  readonly submittedMessage = signal<string | null>(null);
  readonly time = clock;
  readonly dirty = isDirty;

  /** Local answers, seeded from what the server has saved. */
  readonly drafts = linkedSignal<PublicMockInterview | undefined, Record<string, Draft>>({
    source: () => (this.session.hasValue() ? this.session.value() : undefined),
    computation: (s, previous) =>
      Object.fromEntries((s?.questions ?? []).map((q) => [q.id, previous?.value[q.id] ?? {
        text: q.answer ?? '', rating: q.rating, guidance: q.guidance, saved: { text: q.answer ?? '', rating: q.rating },
      }])),
  });

  readonly firstName = computed(() => (this.session.hasValue() ? this.session.value().candidateName.split(' ')[0] : ''));
  readonly totalMinutes = computed(() => (this.session.hasValue() ? this.session.value().questions.reduce((n, q) => n + (q.minutes ?? 3), 0) : 0));
  readonly answered = computed(() => Object.values(this.drafts()).filter((d) => d.text.trim()).length);

  private openedAt = Date.now();
  private readonly now = toSignal(interval(1000).pipe(map(() => Date.now())), { initialValue: Date.now() });
  readonly elapsed = computed(() => (this.now() - this.openedAt) / 1000);

  firstUnanswered(): number {
    const qs = this.session.value()?.questions ?? [];
    const i = qs.findIndex((q) => !this.drafts()[q.id]?.text.trim());
    return i < 0 ? qs.length : i;
  }

  go(i: number): void {
    this.errors.set([]);
    this.openedAt = Date.now();
    this.step.set(Math.max(0, i));
  }

  edit(id: string, change: Partial<Pick<Draft, 'text' | 'rating'>>): void {
    this.drafts.update((all) => ({ ...all, [id]: { ...all[id], ...change } }));
  }

  /** Saves the answer if it changed, then moves to question <code>target</code> (or stays to show its tips when null). */
  leave(q: PublicMockQuestion, target: number | null): void {
    const d = this.drafts()[q.id];
    if (!isDirty(d)) {
      if (target !== null) this.go(target);
      return;
    }
    this.busy.set(true);
    this.api.saveMockAnswer(this.token(), q.id, { text: d.text, rating: d.rating, seconds: Math.round(this.elapsed()) }).subscribe({
      next: (r) => {
        this.busy.set(false);
        this.drafts.update((all) => ({ ...all, [q.id]: { ...all[q.id], guidance: r.guidance, saved: { text: d.text.trim(), rating: d.rating } } }));
        if (target !== null) this.go(target);
      },
      error: (err) => {
        this.busy.set(false);
        this.errors.set(answerErrors(err));
      },
    });
  }

  submit(): void {
    this.busy.set(true);
    this.api.submitMockInterview(this.token()).subscribe({
      next: (r) => {
        this.busy.set(false);
        this.submittedMessage.set(r.message);
        this.session.reload();
      },
      error: (err) => {
        this.busy.set(false);
        this.errors.set(answerErrors(err));
      },
    });
  }

  print(): void {
    window.print();
  }
}
