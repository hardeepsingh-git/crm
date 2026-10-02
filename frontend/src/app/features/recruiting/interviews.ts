import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, input, output, signal } from '@angular/core';
import { FormBuilder, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { CrmApi, UserLookupService } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { MetadataService } from '../../core/metadata.service';
import {
  INTERVIEW_KINDS, InterviewItem, InterviewKind, InterviewRequest, MyInterviews, RECOMMENDATIONS, Recommendation, ScorecardItem,
} from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { confirm } from '../../shared/confirm-dialog';
import { fromLocalInput, toLocalInput } from '../../shared/list';

export const CRITERIA_PICKLIST = 'interview.criteria';
export const KIND_ICONS: Record<InterviewKind, string> = { Phone: 'call', Video: 'videocam', Onsite: 'apartment' };
const RECOMMENDATION_LABELS = Object.fromEntries(RECOMMENDATIONS.map((r) => [r.value, r.label])) as Record<Recommendation, string>;

export function isUrl(value: string | null): boolean {
  return !!value && /^https?:\/\//i.test(value);
}

/** Interviewers may give feedback once the interview has started, unless it was cancelled. */
export function canGiveFeedback(i: InterviewItem, now = new Date()): boolean {
  return i.iAmInterviewer && i.status !== 'Cancelled' && new Date(i.scheduledAt) <= now;
}

/** One scorecard: recommendation, per-criterion ratings and notes. */
@Component({
  selector: 'app-scorecard-view',
  template: `
    @let s = card();
    <div class="card">
      <div class="head">
        <strong>{{ s.interviewerName }}</strong>
        <span class="rec" [class]="s.recommendation">{{ labels[s.recommendation] }}</span>
      </div>
      @if (ratingList().length) {
        <div class="ratings">
          @for (r of ratingList(); track r[0]) { <span>{{ r[0] }} <strong>{{ r[1] }}</strong>/5</span> }
        </div>
      }
      @if (s.notes) { <div class="notes">{{ s.notes }}</div> }
    </div>
  `,
  styles: `
    .card { padding: 8px 0; border-top: 1px dashed var(--mat-sys-outline-variant); }
    .head { display: flex; gap: 8px; align-items: center; }
    .rec { font-size: 12px; padding: 2px 8px; border-radius: 10px; background: var(--mat-sys-surface-container-high); }
    .rec.StrongYes, .rec.Yes { background: var(--crm-success-container); color: var(--crm-on-success-container); }
    .rec.StrongNo, .rec.No { background: var(--crm-danger-container); color: var(--crm-on-danger-container); }
    .ratings { display: flex; flex-wrap: wrap; gap: 4px 16px; font-size: 13px; margin-top: 4px; }
    .notes { white-space: pre-wrap; margin-top: 4px; font-size: 14px; }
  `,
})
export class ScorecardView {
  readonly card = input.required<ScorecardItem>();
  readonly labels = RECOMMENDATION_LABELS;
  readonly ratingList = computed(() => Object.entries(this.card().ratings));
}

export interface ScheduleInterviewData {
  applicationId: string;
  applicationName: string;
  item?: InterviewItem;
}

@Component({
  selector: 'app-schedule-interview-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ data.item ? 'Reschedule interview' : 'Schedule interview' }}</h2>
    <mat-dialog-content>
      <p class="muted">{{ data.applicationName }}</p>
      <form [formGroup]="form" class="form-grid" id="interview-form" (ngSubmit)="save()">
        <mat-form-field appearance="outline">
          <mat-label>Date and time</mat-label>
          <input matInput type="datetime-local" formControlName="scheduledAt" required />
          @if (form.controls.scheduledAt.hasError('required')) { <mat-error>Choose when the interview is</mat-error> }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Duration (minutes)</mat-label>
          <input matInput type="number" formControlName="durationMinutes" min="15" max="480" step="15" />
          @if (form.controls.durationMinutes.invalid) { <mat-error>Between 15 and 480 minutes</mat-error> }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Type</mat-label>
          <mat-select formControlName="kind">
            @for (k of kinds; track k) { <mat-option [value]="k">{{ k }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>{{ form.controls.kind.value === 'Video' ? 'Meeting link' : form.controls.kind.value === 'Phone' ? 'Phone number' : 'Location' }}</mat-label>
          <input matInput formControlName="location" maxlength="500" />
          @if (form.controls.kind.value === 'Video') { <mat-hint>An https:// link</mat-hint> }
        </mat-form-field>
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Interviewers</mat-label>
          <mat-select formControlName="interviewerIds" multiple required>
            @for (u of users.users.value(); track u.id) { <mat-option [value]="u.id">{{ u.fullName }}</mat-option> }
          </mat-select>
          @if (form.controls.interviewerIds.invalid) { <mat-error>Choose 1 to 10 interviewers</mat-error> }
        </mat-form-field>
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Notes for interviewers</mat-label>
          <textarea matInput rows="3" formControlName="notes" maxlength="2000"></textarea>
        </mat-form-field>
        <mat-checkbox class="span-2" formControlName="emailCandidate">Email the candidate an invitation</mat-checkbox>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="interview-form" [disabled]="saving()">{{ data.item ? 'Save' : 'Schedule' }}</button>
    </mat-dialog-actions>
  `,
})
export class ScheduleInterviewDialog {
  readonly data = inject<ScheduleInterviewData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<ScheduleInterviewDialog, InterviewItem>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  private readonly auth = inject(AuthService);
  readonly users = inject(UserLookupService);
  readonly kinds = INTERVIEW_KINDS;
  readonly saving = signal(false);

  private readonly item = this.data.item;
  readonly form = inject(FormBuilder).nonNullable.group({
    scheduledAt: [toLocalInput(this.item?.scheduledAt ?? null), Validators.required],
    durationMinutes: [this.item?.durationMinutes ?? 60, [Validators.required, Validators.min(15), Validators.max(480)]],
    kind: [this.item?.kind ?? ('Video' as InterviewKind)],
    location: [this.item?.location ?? ''],
    interviewerIds: [this.item?.interviewers.map((i) => i.id) ?? (this.auth.user() ? [this.auth.user()!.id] : []),
      [Validators.required, Validators.maxLength(10)]],
    notes: [this.item?.notes ?? ''],
    emailCandidate: [false],
  });

  save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const body: InterviewRequest = {
      applicationId: this.data.applicationId, scheduledAt: fromLocalInput(v.scheduledAt)!, durationMinutes: v.durationMinutes, kind: v.kind,
      location: v.location.trim() || null, notes: v.notes.trim() || null, interviewerIds: v.interviewerIds, emailCandidate: v.emailCandidate,
    };
    this.saving.set(true);
    this.api.saveInterview(this.item?.id ?? null, body).subscribe({
      next: (saved) => {
        if (saved.emailNote) this.notify.warn(`Interview saved. ${saved.emailNote}`);
        else this.notify.success(v.emailCandidate ? 'Interview saved and invitation queued' : 'Interview saved');
        this.ref.close(saved.interview);
      },
      error: (err) => {
        this.saving.set(false);
        this.notify.formError(err);
      },
    });
  }
}

@Component({
  selector: 'app-scorecard-dialog',
  imports: [DatePipe, FormsModule, MatDialogModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Interview feedback</h2>
    <mat-dialog-content>
      <p class="muted">{{ interview.applicationName }} &middot; {{ interview.kind }} interview, {{ interview.scheduledAt | date: 'd MMM y, h:mm a' }}</p>
      <h4>Overall recommendation</h4>
      <mat-button-toggle-group [value]="recommendation()" (change)="recommendation.set($event.value)" aria-label="Recommendation">
        @for (r of recommendations; track r.value) { <mat-button-toggle [value]="r.value">{{ r.label }}</mat-button-toggle> }
      </mat-button-toggle-group>
      @if (criteria.length) {
        <h4>Ratings</h4>
        @for (c of criteria; track c) {
          <div class="rating-row">
            <span>{{ c }}</span>
            <mat-button-toggle-group [value]="ratings()[c]" (change)="rate(c, $event.value)" [attr.aria-label]="c">
              @for (n of scale; track n) { <mat-button-toggle [value]="n">{{ n }}</mat-button-toggle> }
            </mat-button-toggle-group>
          </div>
        }
        <p class="muted small">1 = poor, 5 = excellent. Skip anything you didn't assess.</p>
      }
      <mat-form-field appearance="outline" class="full">
        <mat-label>Notes</mat-label>
        <textarea matInput rows="5" [(ngModel)]="notes" maxlength="4000"></textarea>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="!recommendation() || saving()">Submit feedback</button>
    </mat-dialog-actions>
  `,
  styles: `
    h4 { margin: 16px 0 8px; }
    .rating-row { display: flex; justify-content: space-between; align-items: center; gap: 16px; margin: 6px 0; }
    .full { width: 100%; margin-top: 16px; }
    .small { font-size: 12px; }
  `,
})
export class ScorecardDialog {
  readonly interview = inject<InterviewItem>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<ScorecardDialog, InterviewItem>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly recommendations = RECOMMENDATIONS;
  readonly scale = [1, 2, 3, 4, 5];
  readonly criteria = inject(MetadataService).picklist(CRITERIA_PICKLIST);
  private readonly mine = this.interview.myScorecard;
  readonly recommendation = signal<Recommendation | null>(this.mine?.recommendation ?? null);
  // Criteria removed since the card was submitted are dropped; the server only accepts current ones.
  readonly ratings = signal<Record<string, number>>(
    Object.fromEntries(Object.entries(this.mine?.ratings ?? {}).filter(([k]) => this.criteria.includes(k))));
  notes = this.mine?.notes ?? '';
  readonly saving = signal(false);

  rate(criterion: string, value: number): void {
    this.ratings.update((r) => ({ ...r, [criterion]: value }));
  }

  save(): void {
    this.saving.set(true);
    this.api.submitScorecard(this.interview.id, { recommendation: this.recommendation()!, ratings: this.ratings(), notes: this.notes.trim() || null }).subscribe({
      next: (saved) => {
        this.notify.success('Feedback submitted');
        this.ref.close(saved);
      },
      error: (err) => {
        this.saving.set(false);
        this.notify.formError(err);
      },
    });
  }
}

/** Shared dialog openers and actions for interview lists. */
abstract class InterviewActions {
  protected readonly api = inject(CrmApi);
  protected readonly dialog = inject(MatDialog);
  protected readonly notify = inject(NotifyService);
  readonly icons = KIND_ICONS;
  readonly isUrl = isUrl;
  readonly canGiveFeedback = canGiveFeedback;

  protected abstract changed(): void;

  feedback(i: InterviewItem): void {
    this.dialog.open(ScorecardDialog, { data: i, width: '600px', maxHeight: '90vh' }).afterClosed().subscribe((saved) => saved && this.changed());
  }

  calendar(i: InterviewItem): void {
    this.api.downloadInterviewCalendar(i.id).subscribe();
  }
}

/** Interviews for one application, shown on its record page. */
@Component({
  selector: 'app-interviews-panel',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule, ScorecardView],
  template: `
    <div class="panel-header">
      <h3>Interviews</h3>
      <span class="spacer"></span>
      @if (canSchedule()) {
        <button mat-flat-button (click)="schedule()"><mat-icon>event</mat-icon> Schedule interview</button>
      }
    </div>
    @for (i of items(); track i.id) {
      <div class="interview" [class.cancelled]="i.status === 'Cancelled'">
        <div class="row">
          <mat-icon class="kind">{{ icons[i.kind] }}</mat-icon>
          <div class="body">
            <div class="title">{{ i.kind }} interview &middot; {{ i.scheduledAt | date: 'EEE d MMM y, h:mm a' }} ({{ i.durationMinutes }} min)
              @if (i.status !== 'Scheduled') { <span class="status">{{ i.status }}</span> }
            </div>
            <div class="muted small">
              @if (i.location) {
                @if (isUrl(i.location)) { <a [href]="i.location" target="_blank" rel="noopener">Join meeting</a> } @else { {{ i.location }} }
                &middot;
              }
              @for (p of i.interviewers; track p.id; let last = $last) {
                {{ p.name }}@if (p.submitted) {<mat-icon inline matTooltip="Feedback submitted" class="ok">check_circle</mat-icon>}@if (!last) {, }
              }
            </div>
            @if (i.notes) { <div class="notes">{{ i.notes }}</div> }
          </div>
          @if (canGiveFeedback(i)) {
            <button mat-stroked-button (click)="feedback(i)">{{ i.myScorecard ? 'Edit feedback' : 'Give feedback' }}</button>
          }
          <button mat-icon-button [matMenuTriggerFor]="menu" aria-label="Interview actions"><mat-icon>more_vert</mat-icon></button>
          <mat-menu #menu="matMenu">
            <button mat-menu-item (click)="calendar(i)"><mat-icon>calendar_month</mat-icon> Add to calendar (.ics)</button>
            @if (i.canEdit && i.status === 'Scheduled') {
              <button mat-menu-item (click)="schedule(i)"><mat-icon>edit_calendar</mat-icon> Reschedule</button>
              <button mat-menu-item (click)="setStatus(i, 'complete')"><mat-icon>task_alt</mat-icon> Mark completed</button>
              <button mat-menu-item (click)="setStatus(i, 'cancel')"><mat-icon>event_busy</mat-icon> Cancel interview</button>
            }
          </mat-menu>
        </div>
        @for (s of i.scorecards; track s.id) { <app-scorecard-view [card]="s" /> }
        @if (i.iAmInterviewer && !i.myScorecard && i.status !== 'Cancelled' && hasOtherFeedback(i)) {
          <p class="muted small blind">Colleagues' feedback is hidden until you submit yours.</p>
        }
      </div>
    } @empty {
      <p class="muted">No interviews yet.</p>
    }
  `,
  styles: `
    .panel-header { display: flex; gap: 8px; align-items: center; margin-bottom: 8px; }
    .interview { padding: 8px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .interview.cancelled .title { text-decoration: line-through; opacity: .7; }
    .row { display: flex; gap: 12px; align-items: flex-start; }
    .kind { margin-top: 2px; color: var(--mat-sys-primary); }
    .body { flex: 1; }
    .title { font-weight: 500; }
    .status { font-size: 12px; font-weight: normal; margin-left: 6px; padding: 1px 8px; border-radius: 10px; background: var(--mat-sys-surface-container-high); }
    .small { font-size: 13px; }
    .ok { color: var(--crm-success); vertical-align: middle; }
    .notes { white-space: pre-wrap; font-size: 14px; margin-top: 4px; }
    .blind { margin: 4px 0 0 36px; }
  `,
})
export class InterviewsPanel extends InterviewActions {
  readonly applicationId = input.required<string>();
  readonly applicationName = input.required<string>();
  readonly ownerId = input.required<string>();
  readonly updated = output<void>();
  private readonly auth = inject(AuthService);

  private readonly data = httpResource<InterviewItem[]>(() => ({ url: '/api/interviews', params: { applicationId: this.applicationId() } }), { defaultValue: [] });
  readonly items = computed(() => this.data.value() ?? []);
  readonly canSchedule = computed(() => this.auth.canModify('application', this.ownerId()));

  hasOtherFeedback(i: InterviewItem): boolean {
    return i.interviewers.some((p) => p.submitted);
  }

  protected changed(): void {
    this.data.reload();
    this.updated.emit();
  }

  schedule(item?: InterviewItem): void {
    this.dialog
      .open(ScheduleInterviewDialog, { data: { applicationId: this.applicationId(), applicationName: this.applicationName(), item } satisfies ScheduleInterviewData, width: '640px' })
      .afterClosed()
      .subscribe((saved) => saved && this.changed());
  }

  setStatus(i: InterviewItem, action: 'cancel' | 'complete'): void {
    const cancel = action === 'cancel';
    confirm(this.dialog, {
      title: cancel ? 'Cancel interview?' : 'Mark interview completed?',
      message: cancel
        ? 'Interviewers are notified and the interview is removed from their activities. The candidate is not emailed.'
        : 'The interview is marked as held. Interviewers can still submit feedback.',
      confirmText: cancel ? 'Cancel interview' : 'Mark completed',
      danger: cancel,
    }).subscribe((ok) => {
      if (!ok) return;
      this.api.setInterviewStatus(i.id, action).subscribe(() => {
        this.notify.success(cancel ? 'Interview cancelled' : 'Interview completed');
        this.changed();
      });
    });
  }
}

/** The signed-in user's upcoming interviews and the ones still waiting for their feedback. */
@Component({
  selector: 'app-my-interviews',
  imports: [DatePipe, RouterLink, MatButtonModule, MatCardModule, MatIconModule, MatProgressBarModule],
  template: `
    <div class="page-header"><div><h1>My interviews</h1><div class="muted">Interviews you're on, across all jobs.</div></div></div>
    @if (data.isLoading() && !data.hasValue()) { <mat-progress-bar mode="indeterminate" /> }
    @if (data.hasValue()) {
      @let d = data.value();
      <div class="grid">
        <mat-card appearance="outlined">
          <mat-card-header><mat-card-title>Feedback needed ({{ d.awaitingFeedback.length }})</mat-card-title></mat-card-header>
          <mat-card-content>
            @for (i of d.awaitingFeedback; track i.id) {
              <div class="item">
                <mat-icon class="kind">{{ icons[i.kind] }}</mat-icon>
                <div class="body">
                  <a [routerLink]="['/o/application', i.applicationId]">{{ i.applicationName }}</a>
                  <div class="muted small">{{ i.jobTitle ?? 'No job' }} &middot; {{ i.scheduledAt | date: 'd MMM y, h:mm a' }}</div>
                </div>
                <button mat-flat-button (click)="feedback(i)">Give feedback</button>
              </div>
            } @empty { <p class="muted">You're all caught up.</p> }
          </mat-card-content>
        </mat-card>
        <mat-card appearance="outlined">
          <mat-card-header><mat-card-title>Upcoming ({{ d.upcoming.length }})</mat-card-title></mat-card-header>
          <mat-card-content>
            @for (i of d.upcoming; track i.id) {
              <div class="item">
                <mat-icon class="kind">{{ icons[i.kind] }}</mat-icon>
                <div class="body">
                  <a [routerLink]="['/o/application', i.applicationId]">{{ i.applicationName }}</a>
                  <div class="muted small">
                    {{ i.jobTitle ?? 'No job' }} &middot; {{ i.scheduledAt | date: 'EEE d MMM, h:mm a' }} ({{ i.durationMinutes }} min)
                    @if (i.location) {
                      &middot; @if (isUrl(i.location)) { <a [href]="i.location" target="_blank" rel="noopener">Join meeting</a> } @else { {{ i.location }} }
                    }
                  </div>
                </div>
                <button mat-icon-button (click)="calendar(i)" aria-label="Add to calendar"><mat-icon>calendar_month</mat-icon></button>
              </div>
            } @empty { <p class="muted">No interviews scheduled.</p> }
          </mat-card-content>
        </mat-card>
      </div>
    }
  `,
  styles: `
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(400px, 1fr)); gap: 16px; }
    .item { display: flex; gap: 12px; align-items: center; padding: 10px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .kind { color: var(--mat-sys-primary); }
    .body { flex: 1; }
    .small { font-size: 13px; }
  `,
})
export class MyInterviewsPage extends InterviewActions {
  readonly data = httpResource<MyInterviews>(() => '/api/interviews/mine');

  protected changed(): void {
    this.data.reload();
  }
}
