import { CurrencyPipe, DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, input, output, signal } from '@angular/core';
import { FormBuilder, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { CrmApi } from '../../core/api';
import { OfferItem, OfferPreview, OfferRequest, OfferSent, OfferStatus, PAY_PERIODS, PayPeriod } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { confirm } from '../../shared/confirm-dialog';
import { fromLocalInput, toLocalInput } from '../../shared/list';

export const OFFER_PLACEHOLDERS = ['{{name}}', '{{offer.job_title}}', '{{offer.salary}}', '{{offer.start_date}}', '{{offer.expires}}', '{{owner.name}}', '{{organisation.name}}'];

/** Draft and Sent offers are open; at most one per application. */
export function isOpen(status: OfferStatus): boolean {
  return status === 'Draft' || status === 'Sent';
}

/** "per year" etc. */
export function perPeriod(period: PayPeriod): string {
  return `per ${period.toLowerCase()}`;
}

export interface OfferDialogData {
  applicationId: string;
  item?: OfferItem;
}

@Component({
  selector: 'app-offer-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ data.item ? 'Edit offer' : 'Make an offer' }}</h2>
    <mat-dialog-content>
      @if (loading()) { <mat-progress-bar mode="indeterminate" /> }
      <form [formGroup]="form" class="form-grid" id="offer-form" (ngSubmit)="save()">
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Job title</mat-label>
          <input matInput formControlName="jobTitle" maxlength="200" required />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Salary</mat-label>
          <input matInput type="number" formControlName="salary" min="1" required />
          @if (form.controls.salary.invalid) { <mat-error>Enter the salary</mat-error> }
        </mat-form-field>
        <div class="pair">
          <mat-form-field appearance="outline">
            <mat-label>Currency</mat-label>
            <input matInput formControlName="currency" maxlength="3" required />
            @if (form.controls.currency.invalid) { <mat-error>e.g. AUD</mat-error> }
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Per</mat-label>
            <mat-select formControlName="payPeriod">
              @for (p of periods; track p) { <mat-option [value]="p">{{ p.toLowerCase() }}</mat-option> }
            </mat-select>
          </mat-form-field>
        </div>
        <mat-form-field appearance="outline">
          <mat-label>Start date</mat-label>
          <input matInput type="date" formControlName="startDate" required />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Respond by</mat-label>
          <input matInput type="datetime-local" formControlName="expiresAt" required />
          <mat-hint>Up to 60 days</mat-hint>
        </mat-form-field>
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Offer letter</mat-label>
          <textarea matInput rows="12" formControlName="letter" maxlength="20000" required></textarea>
          <mat-hint>Fields: {{ placeholders }}. They're filled in when you send the offer.</mat-hint>
        </mat-form-field>
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Internal notes</mat-label>
          <textarea matInput rows="2" formControlName="notes" maxlength="2000"></textarea>
          <mat-hint>Never shown to the candidate</mat-hint>
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="offer-form" [disabled]="saving() || loading()">Save draft</button>
    </mat-dialog-actions>
  `,
  styles: `.pair { display: flex; gap: 8px; } .pair mat-form-field { flex: 1; min-width: 0; }`,
})
export class OfferDialog {
  readonly data = inject<OfferDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<OfferDialog, OfferItem>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly periods = PAY_PERIODS;
  readonly placeholders = OFFER_PLACEHOLDERS.join(' ');
  readonly saving = signal(false);
  readonly loading = signal(!this.data.item);

  private readonly item = this.data.item;
  readonly form = inject(FormBuilder).nonNullable.group({
    jobTitle: [this.item?.jobTitle ?? '', [Validators.required, Validators.maxLength(200)]],
    salary: [this.item?.salary ?? (null as number | null), [Validators.required, Validators.min(1)]],
    currency: [this.item?.currency ?? '', [Validators.required, Validators.pattern(/^[A-Z]{3}$/)]],
    payPeriod: [this.item?.payPeriod ?? ('Year' as PayPeriod)],
    startDate: [this.item?.startDate ?? '', Validators.required],
    expiresAt: [toLocalInput(this.item?.expiresAt ?? null), Validators.required],
    letter: [this.item?.letter ?? '', Validators.required],
    notes: [this.item?.notes ?? ''],
  });

  constructor() {
    if (this.item) return;
    this.api.offerDefaults(this.data.applicationId).subscribe({
      next: (d) => {
        this.form.patchValue({
          jobTitle: d.jobTitle, salary: d.salary, currency: d.currency, payPeriod: d.payPeriod, startDate: d.startDate,
          expiresAt: toLocalInput(d.expiresAt), letter: d.letter,
        });
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const body: OfferRequest = {
      applicationId: this.data.applicationId, jobTitle: v.jobTitle.trim(), salary: v.salary!, currency: v.currency.toUpperCase(),
      payPeriod: v.payPeriod, startDate: v.startDate, expiresAt: fromLocalInput(v.expiresAt)!, letter: v.letter, notes: v.notes.trim() || null,
    };
    this.saving.set(true);
    this.api.saveOffer(this.item?.id ?? null, body).subscribe({
      next: (saved) => {
        this.notify.success('Offer saved as a draft');
        this.ref.close(saved);
      },
      error: (err) => {
        this.saving.set(false);
        this.notify.formError(err);
      },
    });
  }
}

@Component({
  selector: 'app-offer-preview-dialog',
  imports: [MatDialogModule, MatButtonModule, MatIconModule],
  template: `
    <h2 mat-dialog-title>Offer letter preview</h2>
    <mat-dialog-content>
      @if (data.blankFields.length) {
        <p class="warn"><mat-icon inline>warning</mat-icon> Empty on this application: {{ data.blankFields.join(', ') }}</p>
      }
      <div class="letter">{{ data.letter }}</div>
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Close</button></mat-dialog-actions>
  `,
  styles: `.letter { white-space: pre-wrap; line-height: 1.6; } .warn { color: var(--mat-sys-error); }`,
})
export class OfferPreviewDialog {
  readonly data = inject<OfferPreview>(MAT_DIALOG_DATA);
}

@Component({
  selector: 'app-offer-sent-dialog',
  imports: [MatDialogModule, MatButtonModule, MatIconModule],
  template: `
    <h2 mat-dialog-title>Offer sent</h2>
    <mat-dialog-content>
      @if (data.emailNote) {
        <p class="warn"><mat-icon inline>warning</mat-icon> {{ data.emailNote }}</p>
      } @else {
        <p>We've emailed the candidate a link to read and answer the offer.</p>
      }
      <p class="muted">This link is shown only now. Sending the offer again creates a new link and stops this one working.</p>
      <div class="link"><code>{{ data.link }}</code></div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-stroked-button (click)="copy()"><mat-icon>content_copy</mat-icon> {{ copied() ? 'Copied' : 'Copy link' }}</button>
      <button mat-flat-button mat-dialog-close>Done</button>
    </mat-dialog-actions>
  `,
  styles: `.link { padding: 8px; background: var(--mat-sys-surface-container-high); border-radius: 4px; word-break: break-all; } .warn { color: var(--mat-sys-error); }`,
})
export class OfferSentDialog {
  readonly data = inject<OfferSent>(MAT_DIALOG_DATA);
  readonly copied = signal(false);

  copy(): void {
    void navigator.clipboard?.writeText(this.data.link).then(() => this.copied.set(true));
  }
}

@Component({
  selector: 'app-withdraw-offer-dialog',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Withdraw offer?</h2>
    <mat-dialog-content>
      <p>The candidate's link stops working straight away. They aren't emailed, so let them know yourself.</p>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Reason (kept on the timeline)</mat-label>
        <textarea matInput rows="3" [(ngModel)]="reason" maxlength="2000"></textarea>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button class="danger" [mat-dialog-close]="{ reason: reason.trim() || null }">Withdraw offer</button>
    </mat-dialog-actions>
  `,
  styles: `.full { width: 100%; }`,
})
export class WithdrawOfferDialog {
  reason = '';
}

/** Offers for one application. Only shown to its hiring team (people who may edit it), as offers carry pay details. */
@Component({
  selector: 'app-offers-panel',
  imports: [CurrencyPipe, DatePipe, MatButtonModule, MatIconModule, MatMenuModule],
  template: `
    <div class="panel-header">
      <h3>Offers</h3>
      <span class="spacer"></span>
      @if (!hasOpen()) { <button mat-flat-button (click)="edit()"><mat-icon>handshake</mat-icon> Make offer</button> }
    </div>
    @for (o of items(); track o.id) {
      <div class="offer">
        <div class="row">
          <div class="body">
            <div class="title">{{ o.jobTitle }} <span class="status" [attr.data-s]="o.status">{{ o.status }}</span></div>
            <div class="muted small">
              {{ o.salary | currency: o.currency : 'symbol' : '1.0-2' }} {{ per(o.payPeriod) }} &middot; starts {{ o.startDate | date: 'd MMM y' }}
              @if (o.status === 'Draft' || o.status === 'Sent') { &middot; respond by {{ o.expiresAt | date: 'd MMM y, h:mm a' }} }
            </div>
            @if (o.sentAt) { <div class="muted small">Sent {{ o.sentAt | date: 'd MMM y, h:mm a' }} by {{ o.sentByName }}</div> }
            @if (o.status === 'Accepted') {
              <div class="small ok">Accepted {{ o.respondedAt | date: 'd MMM y, h:mm a' }}, signed "{{ o.signedName }}"@if (o.responseIp) { from {{ o.responseIp }}}</div>
            }
            @if (o.status === 'Declined') {
              <div class="small">Declined {{ o.respondedAt | date: 'd MMM y' }}@if (o.declineReason) {: {{ o.declineReason }}}</div>
            }
            @if (o.notes) { <div class="muted small">Note: {{ o.notes }}</div> }
          </div>
          @if (o.status === 'Draft') { <button mat-flat-button (click)="send(o)"><mat-icon>send</mat-icon> Send</button> }
          <button mat-icon-button [matMenuTriggerFor]="menu" aria-label="Offer actions"><mat-icon>more_vert</mat-icon></button>
          <mat-menu #menu="matMenu">
            <button mat-menu-item (click)="preview(o)"><mat-icon>description</mat-icon> {{ o.status === 'Draft' ? 'Preview letter' : 'View letter' }}</button>
            @if (o.status === 'Draft') {
              <button mat-menu-item (click)="edit(o)"><mat-icon>edit</mat-icon> Edit</button>
              <button mat-menu-item (click)="remove(o)"><mat-icon>delete</mat-icon> Delete draft</button>
            }
            @if (o.status === 'Sent') {
              <button mat-menu-item (click)="send(o)"><mat-icon>forward_to_inbox</mat-icon> Send a new link</button>
              <button mat-menu-item (click)="withdraw(o)"><mat-icon>block</mat-icon> Withdraw</button>
            }
          </mat-menu>
        </div>
      </div>
    } @empty {
      <p class="muted">No offers yet.</p>
    }
  `,
  styles: `
    .panel-header { display: flex; gap: 8px; align-items: center; margin-bottom: 8px; }
    .offer { padding: 8px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .row { display: flex; gap: 12px; align-items: flex-start; }
    .body { flex: 1; }
    .title { font-weight: 500; }
    .small { font-size: 13px; margin-top: 2px; }
    .ok { color: #2e7d32; }
    .status { font-size: 12px; font-weight: normal; margin-left: 6px; padding: 1px 8px; border-radius: 10px; background: var(--mat-sys-surface-container-high); }
    .status[data-s='Accepted'] { background: #d7f5dd; color: #145a24; }
    .status[data-s='Declined'], .status[data-s='Withdrawn'], .status[data-s='Expired'] { background: #fbdcdc; color: #8a1c1c; }
    .status[data-s='Sent'] { background: #e3ecfd; color: #1a3d8f; }
  `,
})
export class OffersPanel {
  readonly applicationId = input.required<string>();
  readonly updated = output<void>();
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  readonly per = perPeriod;

  private readonly data = httpResource<OfferItem[]>(() => ({ url: '/api/offers', params: { applicationId: this.applicationId() } }), { defaultValue: [] });
  readonly items = computed(() => this.data.value() ?? []);
  readonly hasOpen = computed(() => this.items().some((o) => isOpen(o.status)));

  private changed(): void {
    this.data.reload();
    this.updated.emit();
  }

  edit(item?: OfferItem): void {
    this.dialog
      .open(OfferDialog, { data: { applicationId: this.applicationId(), item } satisfies OfferDialogData, width: '720px', maxHeight: '90vh' })
      .afterClosed()
      .subscribe((saved) => saved && this.changed());
  }

  preview(o: OfferItem): void {
    const open = (p: OfferPreview) => this.dialog.open(OfferPreviewDialog, { data: p, width: '680px', maxHeight: '90vh' });
    if (o.status === 'Draft') this.api.previewOffer(o.id).subscribe(open);
    else open({ letter: o.letter, blankFields: [] });
  }

  send(o: OfferItem): void {
    const resend = o.status === 'Sent';
    confirm(this.dialog, {
      title: resend ? 'Send a new link?' : 'Send this offer?',
      message: resend
        ? 'The candidate gets a new email and link; the previous link stops working.'
        : 'The letter is finalised and the candidate is emailed a link to accept or decline. The application moves to the Offer stage.',
      confirmText: resend ? 'Send new link' : 'Send offer',
    }).subscribe((ok) => {
      if (!ok) return;
      this.api.sendOffer(o.id).subscribe({
        next: (sent) => {
          this.dialog.open(OfferSentDialog, { data: sent, width: '560px' });
          this.changed();
        },
        error: (err) => this.notify.formError(err),
      });
    });
  }

  withdraw(o: OfferItem): void {
    this.dialog
      .open(WithdrawOfferDialog, { width: '480px' })
      .afterClosed()
      .subscribe((result?: { reason: string | null }) => {
        if (!result) return;
        this.api.withdrawOffer(o.id, result.reason).subscribe(() => {
          this.notify.success('Offer withdrawn');
          this.changed();
        });
      });
  }

  remove(o: OfferItem): void {
    confirm(this.dialog, { title: 'Delete draft offer?', message: 'The draft is deleted. Nothing was sent to the candidate.', confirmText: 'Delete', danger: true })
      .subscribe((ok) => ok && this.api.deleteOffer(o.id).subscribe(() => this.changed()));
  }
}
