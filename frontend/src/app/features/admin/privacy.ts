import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, input, linkedSignal, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { Router, RouterLink } from '@angular/router';
import { CrmApi } from '../../core/api';
import { ErasureResult, RetentionStatus } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { confirm } from '../../shared/confirm-dialog';
import { SettingsBack } from './settings-home';

export const RETENTION_OPTIONS = [6, 12, 24, 36, 60, 84];

/** Same rule as the server: case and spacing don't matter. */
export function sameName(a: string, b: string): boolean {
  const norm = (s: string) => s.trim().split(/\s+/).join(' ').toLowerCase();
  return norm(a).length > 0 && norm(a) === norm(b);
}

export interface EraseCandidateData {
  id: string;
  name: string;
}

@Component({
  selector: 'app-erase-candidate-dialog',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Erase {{ data.name }}?</h2>
    <mat-dialog-content>
      <p>This permanently deletes the candidate with all their applications, interviews, scorecards, offers, notes, files and emails,
        and removes their name and details from the audit log. <strong>It can't be undone.</strong></p>
      <p class="muted">Download their data first if they asked for a copy.</p>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Type {{ data.name }} to confirm</mat-label>
        <input matInput [ngModel]="typed()" (ngModelChange)="typed.set($event)" name="confirm" autocomplete="off" />
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button class="danger" (click)="erase()" [disabled]="!matches() || busy()">Erase permanently</button>
    </mat-dialog-actions>
  `,
  styles: `.full { width: 100%; }`,
})
export class EraseCandidateDialog {
  readonly data = inject<EraseCandidateData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<EraseCandidateDialog, ErasureResult>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly busy = signal(false);
  readonly typed = signal('');
  readonly matches = computed(() => sameName(this.typed(), this.data.name));

  erase(): void {
    this.busy.set(true);
    this.api.eraseCandidate(this.data.id, this.typed()).subscribe({
      next: (r) => {
        this.notify.success(`Erased: ${r.applications} applications, ${r.files} files and ${r.emails} emails removed`);
        this.ref.close(r);
      },
      error: (err) => {
        this.busy.set(false);
        this.notify.formError(err);
      },
    });
  }
}

/** Privacy actions on a candidate's page, for organisation administrators. */
@Component({
  selector: 'app-candidate-privacy',
  imports: [MatButtonModule, MatIconModule, MatMenuModule],
  template: `
    <button mat-stroked-button [matMenuTriggerFor]="menu"><mat-icon>privacy_tip</mat-icon> Privacy</button>
    <mat-menu #menu="matMenu">
      <button mat-menu-item (click)="download()"><mat-icon>download</mat-icon> Download their data</button>
      <button mat-menu-item (click)="erase()"><mat-icon>delete_forever</mat-icon> Erase candidate…</button>
    </mat-menu>
  `,
})
export class CandidatePrivacyActions {
  readonly candidateId = input.required<string>();
  readonly candidateName = input.required<string>();
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);

  download(): void {
    this.api.exportCandidateData(this.candidateId()).subscribe(() => this.notify.success('Download started. The export is recorded in the audit log.'));
  }

  erase(): void {
    this.dialog
      .open(EraseCandidateDialog, { data: { id: this.candidateId(), name: this.candidateName() } satisfies EraseCandidateData, width: '520px' })
      .afterClosed()
      .subscribe((result) => result && void this.router.navigate(['/o/candidate']));
  }
}

/** Settings > Privacy: how long candidates are kept, with a preview of who the policy would erase. */
@Component({
  selector: 'app-privacy-settings',
  imports: [DatePipe, RouterLink, MatButtonModule, MatCardModule, MatFormFieldModule, MatIconModule, MatProgressBarModule, MatSelectModule, SettingsBack],
  template: `
    <div class="page-header"><div><app-settings-back /><h1>Privacy</h1></div></div>
    @if (status.error()) {
      <p class="muted">Privacy tools are available for organisations with candidates (the Recruitment template).</p>
    }
    @if (status.isLoading() && !status.hasValue()) { <mat-progress-bar mode="indeterminate" /> }
    @if (status.hasValue()) {
      @let s = status.value();
      <mat-card appearance="outlined">
        <mat-card-header><mat-card-title>Candidate retention</mat-card-title></mat-card-header>
        <mat-card-content>
          <p class="muted">Erase candidates who haven't been active for a while: no changes in the period, every application closed, and none hired.
            Hired people are kept, as their details are now part of employment records.</p>
          <div class="row">
            <mat-form-field appearance="outline">
              <mat-label>Keep candidates for</mat-label>
              <mat-select [value]="months()" (selectionChange)="months.set($event.value)">
                <mat-option [value]="null">No limit (policy off)</mat-option>
                @for (m of options; track m) { <mat-option [value]="m">{{ m }} months</mat-option> }
              </mat-select>
            </mat-form-field>
            <button mat-flat-button (click)="save()" [disabled]="busy() || months() === s.months">Save</button>
          </div>
          @if (s.months) {
            <h3>{{ s.eligible }} {{ s.eligible === 1 ? 'candidate' : 'candidates' }} inactive since {{ s.cutoff | date: 'd MMM y' }}</h3>
            @if (s.sample.length) {
              <table class="table">
                <thead><tr><th>Candidate</th><th>Last activity</th><th class="num">Applications</th></tr></thead>
                <tbody>
                  @for (c of s.sample; track c.id) {
                    <tr><td><a [routerLink]="['/o/candidate', c.id]">{{ c.name }}</a></td><td>{{ c.lastActivity | date: 'd MMM y' }}</td><td class="num">{{ c.applications }}</td></tr>
                  }
                </tbody>
              </table>
              @if (s.eligible > s.sample.length) { <p class="muted small">Showing the {{ s.sample.length }} longest inactive.</p> }
              <button mat-flat-button class="danger" (click)="run(s)" [disabled]="busy()"><mat-icon>delete_sweep</mat-icon> Erase {{ s.eligible > batch ? 'the first ' + batch : 'them' }} now</button>
            } @else {
              <p class="muted">Nobody to erase at the moment.</p>
            }
          }
        </mat-card-content>
      </mat-card>
      <mat-card appearance="outlined" class="help">
        <mat-card-header><mat-card-title>Requests from candidates</mat-card-title></mat-card-header>
        <mat-card-content>
          <p class="muted">Open the candidate and use <strong>Privacy</strong>: <em>Download their data</em> gives a ZIP with everything held about
            them (records, interviews, offers, notes, emails and files), and <em>Erase candidate</em> removes it all. Both are recorded in the audit log
            without personal details.</p>
        </mat-card-content>
      </mat-card>
    }
  `,
  styles: `
    .row { display: flex; gap: 12px; align-items: baseline; flex-wrap: wrap; }
    .help { margin-top: 16px; }
    .table { width: 100%; border-collapse: collapse; font-size: 14px; margin: 8px 0 16px; }
    .table th, .table td { padding: 8px; border-bottom: 1px solid var(--mat-sys-outline-variant); text-align: left; }
    .table .num { text-align: right; }
    .small { font-size: 13px; }
  `,
})
export class PrivacySettings {
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  readonly options = RETENTION_OPTIONS;
  readonly batch = 100;
  readonly busy = signal(false);
  readonly status = httpResource<RetentionStatus>(() => '/api/privacy/retention');
  /** The choice in the form, reset to the saved policy whenever it (re)loads. */
  readonly months = linkedSignal(() => (this.status.hasValue() ? this.status.value().months : null));

  save(): void {
    this.busy.set(true);
    this.api.saveRetention(this.months()).subscribe({
      next: (s) => {
        this.busy.set(false);
        this.status.set(s);
        this.notify.success(s.months ? `Candidates are kept for ${s.months} months` : 'Retention policy switched off');
      },
      error: (err) => {
        this.busy.set(false);
        this.notify.formError(err);
      },
    });
  }

  run(s: RetentionStatus): void {
    confirm(this.dialog, {
      title: `Erase ${Math.min(s.eligible, this.batch)} candidates?`,
      message: 'Their applications, interviews, offers, notes, files and emails are deleted, and their details removed from the audit log. This cannot be undone.',
      confirmText: 'Erase permanently',
      danger: true,
    }).subscribe((ok) => {
      if (!ok) return;
      this.busy.set(true);
      this.api.runRetention().subscribe({
        next: (r) => {
          this.busy.set(false);
          this.notify.success(`${r.erased} candidates erased${r.remaining ? `; ${r.remaining} left, run again to continue` : ''}`);
          this.status.reload();
        },
        error: () => this.busy.set(false),
      });
    });
  }
}
