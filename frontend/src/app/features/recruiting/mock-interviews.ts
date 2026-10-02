import { DatePipe, DecimalPipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, input, linkedSignal, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { CrmApi } from '../../core/api';
import { MockInterviewItem, MockInterviewList, MockInterviewStatus, MockInviteSent, QuestionLibrary } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { confirm } from '../../shared/confirm-dialog';

export const MAX_MOCK_QUESTIONS = 20;
export const MOCK_VALIDITY_DAYS = [3, 7, 14, 30];

/** Sent and InProgress sessions are open; at most one per application. */
export function isMockOpen(status: MockInterviewStatus): boolean {
  return status === 'Sent' || status === 'InProgress';
}

const STATUS_LABELS: Record<MockInterviewStatus, string> = {
  Sent: 'Waiting for candidate',
  InProgress: 'In progress',
  Submitted: 'Completed',
  Expired: 'Expired',
  Withdrawn: 'Withdrawn',
};

export function mockStatusLabel(status: MockInterviewStatus): string {
  return STATUS_LABELS[status];
}

/** Semantic colour for a status pill: success, info, warning, danger or neutral. */
export function mockTone(status: MockInterviewStatus): string {
  return status === 'Submitted' ? 'success' : status === 'InProgress' ? 'warning' : status === 'Sent' ? 'info' : 'neutral';
}

/** Readable band for a 1–5 score. */
export function scoreBand(score: number | null): string {
  if (score === null) return '';
  return score >= 4.5 ? 'Excellent' : score >= 3.5 ? 'Strong' : score >= 2.5 ? 'Developing' : 'Needs work';
}

@Component({
  selector: 'app-mock-invite-dialog',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Invite to a mock interview</h2>
    <mat-dialog-content>
      <p class="muted intro">The candidate practises on their own with questions from your organisation's library and sees tips after each answer. You can review their answers here.</p>
      @if (library.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      @if (library.hasValue()) {
        @let lib = library.value();
        @if (!lib.available) {
          <p class="notice">Your organisation doesn't have a practice question library yet. Ask an administrator to upgrade the recruitment template.</p>
        } @else if (lib.questions.length === 0) {
          <div class="empty">
            <mat-icon>quiz</mat-icon>
            <p>The question library is empty.</p>
            @if (lib.canAdd) {
              <button mat-flat-button (click)="addStarter()" [disabled]="busy()"><mat-icon>auto_awesome</mat-icon> Add 10 starter questions</button>
            } @else {
              <p class="muted">Ask someone who can add practice questions to set it up.</p>
            }
          </div>
        } @else {
          <div class="list-head">
            <strong>Questions</strong>
            <span class="muted">{{ chosen().size }} of {{ lib.questions.length }} chosen &middot; about {{ minutes() }} min</span>
            <span class="spacer"></span>
            <button mat-button (click)="selectAll(true)">All</button>
            <button mat-button (click)="selectAll(false)">None</button>
          </div>
          <div class="questions" role="group" aria-label="Questions">
            @for (q of lib.questions; track q.id) {
              <mat-checkbox [checked]="chosen().has(q.id)" (change)="toggle(q.id)">
                <span class="q">{{ q.prompt }}</span>
                <span class="meta">
                  @if (q.forThisJob) { <span class="tag job">This job</span> }
                  @if (q.competency) { <span class="tag">{{ q.competency }}</span> }
                  @if (q.minutes) { <span class="muted">{{ q.minutes }} min</span> }
                </span>
              </mat-checkbox>
            }
          </div>
          @if (chosen().size > max) { <p class="warn">Choose at most {{ max }} questions.</p> }
          <div class="row">
            <mat-form-field appearance="outline">
              <mat-label>Link works for</mat-label>
              <mat-select [(ngModel)]="validDays">
                @for (d of days; track d) { <mat-option [value]="d">{{ d }} days</mat-option> }
              </mat-select>
            </mat-form-field>
          </div>
          <mat-form-field appearance="outline" class="full">
            <mat-label>Message to the candidate (optional)</mat-label>
            <textarea matInput rows="2" [(ngModel)]="message" maxlength="2000"></textarea>
          </mat-form-field>
        }
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="send()" [disabled]="busy() || chosen().size === 0 || chosen().size > max"><mat-icon>send</mat-icon> Send invitation</button>
    </mat-dialog-actions>
  `,
  styles: `
    .intro { margin-top: 0; }
    .list-head { display: flex; align-items: center; gap: 8px; margin: 8px 0; }
    .questions { display: flex; flex-direction: column; gap: 2px; max-height: 320px; overflow: auto; padding: 4px; border: 1px solid var(--mat-sys-outline-variant); border-radius: 12px; margin-bottom: 16px; }
    .q { display: block; }
    .meta { display: flex; gap: 6px; align-items: center; font-size: 12px; margin-top: 2px; }
    .tag { padding: 0 8px; border-radius: 10px; background: var(--mat-sys-surface-container-high); }
    .tag.job { background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container); }
    .row { display: flex; gap: 12px; }
    .full { width: 100%; }
    .empty { text-align: center; padding: 24px; }
    .empty mat-icon { font-size: 40px; width: 40px; height: 40px; color: var(--mat-sys-primary); }
    .warn, .notice { color: var(--mat-sys-error); }
  `,
})
export class MockInviteDialog {
  readonly data = inject<{ applicationId: string }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<MockInviteDialog, MockInviteSent>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly max = MAX_MOCK_QUESTIONS;
  readonly days = MOCK_VALIDITY_DAYS;
  readonly busy = signal(false);
  validDays = 7;
  message = '';

  readonly library = httpResource<QuestionLibrary>(() => ({ url: '/api/mock-interviews/library', params: { applicationId: this.data.applicationId } }));
  /** Starts with the first five (job-specific questions come first) and keeps the choice across library reloads. */
  readonly chosen = linkedSignal<QuestionLibrary | undefined, Set<string>>({
    source: () => (this.library.hasValue() ? this.library.value() : undefined),
    computation: (lib, previous) => {
      const ids = new Set(lib?.questions.map((q) => q.id) ?? []);
      const kept = [...(previous?.value ?? [])].filter((id) => ids.has(id));
      return new Set(kept.length ? kept : (lib?.questions.slice(0, 5).map((q) => q.id) ?? []));
    },
  });
  readonly minutes = computed(() => {
    if (!this.library.hasValue()) return 0;
    return this.library.value().questions.filter((q) => this.chosen().has(q.id)).reduce((sum, q) => sum + (q.minutes ?? 3), 0);
  });

  toggle(id: string): void {
    this.chosen.update((s) => {
      const next = new Set(s);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  selectAll(on: boolean): void {
    this.chosen.set(new Set(on && this.library.hasValue() ? this.library.value().questions.slice(0, this.max).map((q) => q.id) : []));
  }

  addStarter(): void {
    this.busy.set(true);
    this.api.addStarterQuestions().subscribe({
      next: (r) => {
        this.busy.set(false);
        this.notify.success(`${r.added} starter questions added to your library`);
        this.library.reload();
      },
      error: () => this.busy.set(false),
    });
  }

  send(): void {
    if (!this.library.hasValue()) return;
    // Keep the library's order so job-specific questions come first.
    const ids = this.library.value().questions.filter((q) => this.chosen().has(q.id)).map((q) => q.id);
    this.busy.set(true);
    this.api.inviteToMockInterview({ applicationId: this.data.applicationId, questionIds: ids, validDays: this.validDays, message: this.message.trim() || null }).subscribe({
      next: (sent) => this.ref.close(sent),
      error: (err) => {
        this.busy.set(false);
        this.notify.formError(err);
      },
    });
  }
}

@Component({
  selector: 'app-mock-sent-dialog',
  imports: [MatDialogModule, MatButtonModule, MatIconModule],
  template: `
    <h2 mat-dialog-title>Mock interview sent</h2>
    <mat-dialog-content>
      @if (data.emailNote) {
        <p class="warn"><mat-icon inline>warning</mat-icon> {{ data.emailNote }}</p>
      } @else {
        <p>We've emailed the candidate a link to their practice interview.</p>
      }
      <p class="muted">This link is shown only now. Sending again creates a new link and stops this one working.</p>
      <div class="link"><code>{{ data.link }}</code></div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-stroked-button (click)="copy()"><mat-icon>content_copy</mat-icon> {{ copied() ? 'Copied' : 'Copy link' }}</button>
      <button mat-flat-button mat-dialog-close>Done</button>
    </mat-dialog-actions>
  `,
  styles: `.link { padding: 8px; background: var(--mat-sys-surface-container-high); border-radius: 8px; word-break: break-all; } .warn { color: var(--mat-sys-error); }`,
})
export class MockSentDialog {
  readonly data = inject<MockInviteSent>(MAT_DIALOG_DATA);
  readonly copied = signal(false);

  copy(): void {
    void navigator.clipboard?.writeText(this.data.link).then(() => this.copied.set(true));
  }
}

export interface MockReviewData {
  item: MockInterviewItem;
  aiAvailable: boolean;
  aiUnavailableReason: string | null;
}

/** The candidate's answers with tips, AI feedback (when set up) and the hiring team's own review. */
@Component({
  selector: 'app-mock-review-dialog',
  imports: [DatePipe, DecimalPipe, FormsModule, MatDialogModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatIconModule, MatInputModule, MatTooltipModule],
  template: `
    <h2 mat-dialog-title>Mock interview: {{ item().jobTitle }}</h2>
    <mat-dialog-content>
      <div class="summary">
        <div><span class="k">Answered</span><span class="v">{{ item().answeredCount }} / {{ item().questionCount }}</span></div>
        @if (item().minutesTaken !== null) { <div><span class="k">Time</span><span class="v">{{ item().minutesTaken }} min</span></div> }
        @if (item().averageSelfRating !== null) { <div><span class="k">Self-rated confidence</span><span class="v">{{ item().averageSelfRating | number: '1.1-1' }} / 5</span></div> }
        @if (item().aiScore !== null) { <div><span class="k">AI score</span><span class="v">{{ item().aiScore | number: '1.1-1' }} / 5 <small>{{ band(item().aiScore) }}</small></span></div> }
      </div>

      <section class="ai" aria-label="AI feedback">
        <div class="ai-head"><mat-icon>auto_awesome</mat-icon><strong>AI feedback</strong><span class="spacer"></span>
          @switch (item().aiStatus) {
            @case ('Pending') { <span class="muted">Working on it… it appears here in a minute or two.</span> }
            @case ('Completed') { <span class="muted">{{ item().aiModel }} &middot; {{ item().aiReviewedAt | date: 'd MMM, h:mm a' }}</span> }
          }
          @if (item().aiStatus !== 'Pending') {
            <button mat-stroked-button (click)="askAi()" [disabled]="!data.aiAvailable || busy()" [matTooltip]="data.aiAvailable ? '' : (data.aiUnavailableReason ?? '')">
              {{ item().aiStatus === 'Completed' || item().aiStatus === 'Failed' ? 'Run again' : 'Get AI feedback' }}
            </button>
          }
        </div>
        @if (item().aiStatus === 'Failed') { <p class="warn">{{ item().aiError }}</p> }
        @if (item().aiStatus === 'NotRequested' && !data.aiAvailable) { <p class="muted small">{{ data.aiUnavailableReason }}</p> }
        @if (item().aiSummary) {
          <p>{{ item().aiSummary }}</p>
          <div class="lists">
            @if (item().aiStrengths.length) { <div><div class="k">Strengths</div><ul>@for (s of item().aiStrengths; track s) { <li>{{ s }}</li> }</ul></div> }
            @if (item().aiImprovements.length) { <div><div class="k">To work on</div><ul>@for (s of item().aiImprovements; track s) { <li>{{ s }}</li> }</ul></div> }
          </div>
          <p class="muted small">AI feedback is a starting point for your own judgement. It never sees the candidate's name or contact details.</p>
        }
      </section>

      @for (r of item().questions; track r.question.id; let i = $index) {
        <article class="qa">
          <div class="q"><span class="n">{{ i + 1 }}</span> {{ r.question.prompt }}
            @if (r.question.competency) { <span class="tag">{{ r.question.competency }}</span> }
          </div>
          @if (r.answer) {
            <div class="a">{{ r.answer.text }}</div>
            <div class="muted small">
              @if (r.answer.rating) { Confidence {{ r.answer.rating }}/5 &middot; }
              @if (r.answer.seconds) { {{ (r.answer.seconds / 60) | number: '1.0-1' }} min }
            </div>
          } @else {
            <div class="muted">Not answered</div>
          }
          @if (r.ai) { <div class="ai-note"><mat-icon inline>auto_awesome</mat-icon> <strong>{{ r.ai.score }}/5</strong> {{ r.ai.feedback }}</div> }
        </article>
      }

      <section class="review">
        <h3>Your review</h3>
        <p class="muted small">Internal to the hiring team; never shown to the candidate.</p>
        <div class="readiness">
          <span id="readiness-label">Interview readiness</span>
          <mat-button-toggle-group [(ngModel)]="rating" aria-labelledby="readiness-label" hideSingleSelectionIndicator>
            @for (n of [1, 2, 3, 4, 5]; track n) { <mat-button-toggle [value]="n">{{ n }}</mat-button-toggle> }
          </mat-button-toggle-group>
          <span class="muted">{{ band(rating) }}</span>
        </div>
        <mat-form-field appearance="outline" class="full">
          <mat-label>Notes</mat-label>
          <textarea matInput rows="3" [(ngModel)]="notes" maxlength="4000" placeholder="What to probe in the real interview"></textarea>
        </mat-form-field>
        @if (item().reviewedByName) { <p class="muted small">Last reviewed by {{ item().reviewedByName }}, {{ item().reviewedAt | date: 'd MMM y, h:mm a' }}</p> }
      </section>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button [mat-dialog-close]="changed">Close</button>
      <button mat-flat-button (click)="save()" [disabled]="busy() || (rating === null && !notes.trim())">Save review</button>
    </mat-dialog-actions>
  `,
  styles: `
    .summary { display: flex; flex-wrap: wrap; gap: 12px; margin-bottom: 16px; }
    .summary > div { flex: 1 1 140px; padding: 12px; border-radius: 12px; background: var(--mat-sys-surface-container); }
    .k { display: block; font-size: 12px; color: var(--mat-sys-on-surface-variant); }
    .v { font: var(--mat-sys-title-medium); }
    .ai { padding: 12px 16px; border-radius: 12px; background: var(--crm-ai-surface); margin-bottom: 16px; }
    .ai-head { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .ai-head mat-icon { color: var(--mat-sys-tertiary); }
    .lists { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .lists ul { margin: 4px 0; padding-left: 20px; }
    .qa { padding: 12px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .q { font-weight: 500; display: flex; gap: 8px; align-items: baseline; flex-wrap: wrap; }
    .n { display: inline-grid; place-items: center; min-width: 22px; height: 22px; border-radius: 11px; font-size: 12px; background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container); }
    .tag { font-size: 12px; font-weight: normal; padding: 0 8px; border-radius: 10px; background: var(--mat-sys-surface-container-high); }
    .a { white-space: pre-wrap; margin: 6px 0 4px; }
    .ai-note { margin-top: 6px; font-size: 13px; padding: 8px; border-radius: 8px; background: var(--crm-ai-surface); }
    .review { margin-top: 16px; }
    .readiness { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin: 8px 0 16px; }
    .full { width: 100%; }
    .small { font-size: 12px; }
    .warn { color: var(--mat-sys-error); }
    @media (max-width: 600px) { .lists { grid-template-columns: 1fr; } }
  `,
})
export class MockReviewDialog {
  readonly data = inject<MockReviewData>(MAT_DIALOG_DATA);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly item = signal(this.data.item);
  readonly busy = signal(false);
  readonly band = scoreBand;
  rating: number | null = this.data.item.readinessRating;
  notes = this.data.item.reviewNotes ?? '';
  changed = false;

  save(): void {
    this.busy.set(true);
    this.api.reviewMockInterview(this.item().id, { readinessRating: this.rating, notes: this.notes.trim() || null }).subscribe({
      next: (updated) => {
        this.busy.set(false);
        this.item.set(updated);
        this.changed = true;
        this.notify.success('Review saved');
      },
      error: (err) => {
        this.busy.set(false);
        this.notify.formError(err);
      },
    });
  }

  askAi(): void {
    this.busy.set(true);
    this.api.requestAiReview(this.item().id).subscribe({
      next: (updated) => {
        this.busy.set(false);
        this.item.set(updated);
        this.changed = true;
      },
      error: (err) => {
        this.busy.set(false);
        this.notify.formError(err);
      },
    });
  }
}

/** Mock interviews for one application; shown to its hiring team (people who may edit it). */
@Component({
  selector: 'app-mock-interviews-panel',
  imports: [DatePipe, DecimalPipe, MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule],
  template: `
    <div class="panel-header">
      <h3>Mock interviews</h3>
      <span class="muted hint">Practice rounds candidates take on their own</span>
      <span class="spacer"></span>
      @if (!hasOpen()) { <button mat-flat-button (click)="invite()"><mat-icon>quiz</mat-icon> Invite to practise</button> }
    </div>
    @for (m of items(); track m.id) {
      <div class="mock">
        <div class="body">
          <div class="title">
            {{ m.questionCount }} questions
            <span class="status-pill" [attr.data-tone]="tone(m.status)">{{ label(m.status) }}</span>
            @if (m.aiStatus === 'Completed' && m.aiScore !== null) { <span class="status-pill" data-tone="ai" matTooltip="AI score"><mat-icon inline>auto_awesome</mat-icon> {{ m.aiScore | number: '1.1-1' }}</span> }
            @if (m.aiStatus === 'Pending') { <span class="status-pill" data-tone="ai">AI feedback pending</span> }
            @if (m.readinessRating) { <span class="status-pill" data-tone="neutral">Readiness {{ m.readinessRating }}/5</span> }
          </div>
          <div class="muted small">
            Sent {{ m.sentAt | date: 'd MMM y' }} by {{ m.sentByName }}
            @if (isOpen(m.status)) { &middot; link works until {{ m.expiresAt | date: 'd MMM y, h:mm a' }} }
            @if (m.submittedAt) { &middot; completed {{ m.submittedAt | date: 'd MMM y, h:mm a' }} &middot; {{ m.answeredCount }}/{{ m.questionCount }} answered }
            @if (m.status === 'InProgress') { &middot; {{ m.answeredCount }}/{{ m.questionCount }} answered so far }
          </div>
        </div>
        @if (m.status === 'Submitted') { <button mat-flat-button (click)="review(m)"><mat-icon>rate_review</mat-icon> Review</button> }
        @if (isOpen(m.status)) {
          <button mat-icon-button [matMenuTriggerFor]="menu" aria-label="Mock interview actions"><mat-icon>more_vert</mat-icon></button>
          <mat-menu #menu="matMenu">
            <button mat-menu-item (click)="resend(m)"><mat-icon>forward_to_inbox</mat-icon> Send a new link</button>
            <button mat-menu-item (click)="withdraw(m)"><mat-icon>block</mat-icon> Withdraw</button>
          </mat-menu>
        }
      </div>
    } @empty {
      <p class="muted">No mock interviews yet. Invite the candidate to practise before their interview.</p>
    }
  `,
  styles: `
    .panel-header { display: flex; gap: 8px; align-items: center; margin-bottom: 8px; flex-wrap: wrap; }
    .hint { font-size: 13px; }
    .mock { display: flex; gap: 12px; align-items: center; padding: 10px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .body { flex: 1; min-width: 0; }
    .title { font-weight: 500; display: flex; gap: 6px; align-items: center; flex-wrap: wrap; }
    .small { font-size: 13px; margin-top: 2px; }
  `,
})
export class MockInterviewsPanel {
  readonly applicationId = input.required<string>();
  readonly updated = output<void>();
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  readonly isOpen = isMockOpen;
  readonly label = mockStatusLabel;
  readonly tone = mockTone;

  private readonly data = httpResource<MockInterviewList>(() => ({ url: '/api/mock-interviews', params: { applicationId: this.applicationId() } }));
  readonly items = computed(() => (this.data.hasValue() ? this.data.value().items : []));
  readonly hasOpen = computed(() => this.items().some((m) => isMockOpen(m.status)));

  private changed(): void {
    this.data.reload();
    this.updated.emit();
  }

  invite(): void {
    this.dialog
      .open(MockInviteDialog, { data: { applicationId: this.applicationId() }, width: '720px', maxHeight: '90vh' })
      .afterClosed()
      .subscribe((sent?: MockInviteSent) => {
        if (!sent) return;
        this.dialog.open(MockSentDialog, { data: sent, width: '560px' });
        this.changed();
      });
  }

  review(m: MockInterviewItem): void {
    const list = this.data.value();
    this.dialog
      .open(MockReviewDialog, {
        data: { item: m, aiAvailable: list?.aiAvailable ?? false, aiUnavailableReason: list?.aiUnavailableReason ?? null } satisfies MockReviewData,
        width: '820px',
        maxHeight: '90vh',
      })
      .afterClosed()
      .subscribe((changed) => changed && this.data.reload());
  }

  resend(m: MockInterviewItem): void {
    confirm(this.dialog, {
      title: 'Send a new link?',
      message: 'The candidate gets a new email and link; the previous link stops working. Answers saved so far are kept.',
      confirmText: 'Send new link',
    }).subscribe((ok) => {
      if (!ok) return;
      this.api.resendMockInterview(m.id).subscribe({
        next: (sent) => {
          this.dialog.open(MockSentDialog, { data: sent, width: '560px' });
          this.changed();
        },
        error: (err) => this.notify.formError(err),
      });
    });
  }

  withdraw(m: MockInterviewItem): void {
    confirm(this.dialog, { title: 'Withdraw mock interview?', message: "The candidate's link stops working straight away.", confirmText: 'Withdraw', danger: true })
      .subscribe((ok) => ok && this.api.withdrawMockInterview(m.id, null).subscribe(() => {
        this.notify.success('Mock interview withdrawn');
        this.changed();
      }));
  }
}
