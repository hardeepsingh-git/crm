import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { CrmApi, stableValue, toParams } from '../../core/api';
import { MetadataService } from '../../core/metadata.service';
import { EmailLogPage, OutboundEmailDetail, OutboundEmailItem, OutboundEmailStatus } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SettingsBack } from './settings-home';

export const EMAIL_STATUSES: OutboundEmailStatus[] = ['Pending', 'Sent', 'Failed', 'Cancelled'];

@Component({
  selector: 'app-email-detail-dialog',
  imports: [DatePipe, MatButtonModule, MatDialogModule],
  template: `
    <h2 mat-dialog-title>{{ d.email.subject }}</h2>
    <mat-dialog-content>
      <dl class="fields">
        <dt>To</dt><dd>{{ d.email.to }}</dd>
        @if (d.replyTo) { <dt>Reply to</dt><dd>{{ d.replyTo }}</dd> }
        <dt>Sent by</dt><dd>{{ d.email.sentByName }} @if (d.ruleName) { (rule "{{ d.ruleName }}") }</dd>
        <dt>Status</dt><dd>{{ d.email.status }} @if (d.email.sentAt) { &middot; {{ d.email.sentAt | date: 'medium' }} }</dd>
        @if (d.email.lastError) { <dt>Error</dt><dd>{{ d.email.lastError }}</dd> }
      </dl>
      <pre class="body">{{ d.body }}</pre>
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Close</button></mat-dialog-actions>
  `,
  styles: `.body { white-space: pre-wrap; font-family: inherit; border-top: 1px solid var(--mat-sys-outline-variant); padding-top: 12px; }`,
})
export class EmailDetailDialog {
  readonly d = inject<OutboundEmailDetail>(MAT_DIALOG_DATA);
}

@Component({
  selector: 'app-email-log',
  imports: [
    DatePipe, FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatPaginatorModule,
    MatProgressBarModule, MatSelectModule, MatTableModule, MatTooltipModule, SettingsBack,
  ],
  template: `
    <div class="page-header"><div><app-settings-back /><h1>Email log</h1></div></div>
    @if (page(); as p) {
      @if (p.deliveryMode === 'Log') {
        <p class="warning"><mat-icon inline>info</mat-icon> No email provider is configured, so emails are written to the server log instead of being delivered. A platform operator can set one up under Platform settings.</p>
      }
      <p class="muted">Every email sent from a record or by automation. Failed emails are retried automatically up to 5 times. Today: {{ p.queuedToday }} of {{ p.dailyLimit }} allowed.</p>
    }
    <div class="filters">
      <mat-form-field appearance="outline" subscriptSizing="dynamic">
        <mat-label>Search</mat-label>
        <input matInput [ngModel]="search()" (ngModelChange)="search.set($event); pageIndex.set(0)" placeholder="Recipient, subject or record" />
      </mat-form-field>
      <mat-form-field appearance="outline" subscriptSizing="dynamic">
        <mat-label>Status</mat-label>
        <mat-select [ngModel]="status()" (ngModelChange)="status.set($event); pageIndex.set(0)">
          <mat-option [value]="null">All</mat-option>
          @for (s of statuses; track s) { <mat-option [value]="s">{{ s }}</mat-option> }
        </mat-select>
      </mat-form-field>
    </div>
    <div class="table-wrap">
      @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      <table mat-table [dataSource]="page()?.page?.items ?? []">
        <ng-container matColumnDef="createdAt">
          <th mat-header-cell *matHeaderCellDef>Queued</th>
          <td mat-cell *matCellDef="let e">{{ e.createdAt | date: 'short' }}</td>
        </ng-container>
        <ng-container matColumnDef="email">
          <th mat-header-cell *matHeaderCellDef>Email</th>
          <td mat-cell *matCellDef="let e">
            <a href="" (click)="$event.preventDefault(); view(e)">{{ e.subject }}</a>
            <div class="muted">to {{ e.to }} &middot; by {{ e.sentByName }}</div>
          </td>
        </ng-container>
        <ng-container matColumnDef="record">
          <th mat-header-cell *matHeaderCellDef>Record</th>
          <td mat-cell *matCellDef="let e">
            @if (e.objectKey && e.recordId) { <a [routerLink]="meta.route(e.objectKey, e.recordId)">{{ e.recordName || meta.label(e.objectKey) }}</a> }
          </td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>Status</th>
          <td mat-cell *matCellDef="let e">
            <span class="status" [class]="e.status.toLowerCase()" [matTooltip]="e.lastError ?? ''">{{ e.status }}</span>
            @if (e.attempts > 1 || (e.status === 'Pending' && e.attempts)) { <div class="muted">{{ e.attempts }} attempts</div> }
          </td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let e" class="actions">
            @if (e.status === 'Failed') { <button mat-stroked-button (click)="retry(e)"><mat-icon>refresh</mat-icon> Retry</button> }
            @if (e.status === 'Pending') { <button mat-button (click)="cancel(e)">Cancel</button> }
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
        <tr class="mat-mdc-row" *matNoDataRow><td class="mat-mdc-cell empty" colspan="5">No emails.</td></tr>
      </table>
      <mat-paginator [length]="page()?.page?.totalCount ?? 0" [pageIndex]="pageIndex()" [pageSize]="25" [hidePageSize]="true" (page)="onPage($event)" />
    </div>
  `,
  styles: `
    .filters { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 12px; }
    .warning { background: var(--mat-sys-secondary-container); padding: 8px 12px; border-radius: 8px; }
    .status { padding: 2px 8px; border-radius: 12px; font-size: 12px; background: var(--mat-sys-surface-container-high); }
    .status.sent { background: #e8f5e9; color: #1b5e20; }
    .status.failed { background: #ffebee; color: #b71c1c; }
    .status.pending { background: #fff8e1; color: #8d6e00; }
    td.actions { text-align: right; white-space: nowrap; }
  `,
})
export class EmailLog {
  readonly meta = inject(MetadataService);
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  readonly statuses = EMAIL_STATUSES;
  readonly columns = ['createdAt', 'email', 'record', 'status', 'actions'];
  readonly search = signal('');
  readonly status = signal<OutboundEmailStatus | null>(null);
  readonly pageIndex = signal(0);

  readonly data = httpResource<EmailLogPage>(() => ({
    url: '/api/admin/emails',
    params: toParams({ search: this.search().trim(), status: this.status(), page: this.pageIndex() + 1, pageSize: 25 }),
  }));
  readonly page = stableValue(this.data);

  onPage(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
  }

  view(e: OutboundEmailItem): void {
    this.api.emailDetail(e.id).subscribe((d) => this.dialog.open(EmailDetailDialog, { data: d, width: '720px', maxHeight: '90vh' }));
  }

  retry(e: OutboundEmailItem): void {
    this.api.retryEmail(e.id).subscribe({
      next: () => {
        this.notify.success('Email queued again');
        this.data.reload();
      },
      error: (err) => this.notify.formError(err),
    });
  }

  cancel(e: OutboundEmailItem): void {
    this.api.cancelEmail(e.id).subscribe({
      next: () => {
        this.notify.success('Email cancelled');
        this.data.reload();
      },
      error: (err) => this.notify.formError(err),
    });
  }
}
