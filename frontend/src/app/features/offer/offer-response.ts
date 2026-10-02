import { CurrencyPipe, DatePipe } from '@angular/common';
import { HttpErrorResponse, httpResource } from '@angular/common/http';
import { Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { CrmApi } from '../../core/api';
import { PublicOffer } from '../../core/models';
import { perPeriod } from '../recruiting/offers';

/** Messages for a failed answer; the server explains expired or already-answered offers. */
export function answerErrors(err: unknown): string[] {
  if (!(err instanceof HttpErrorResponse)) return ['Something went wrong. Please try again.'];
  if (err.status === 404) return ["This link isn't valid any more. It may have been replaced by a newer email."];
  if (err.status === 429) return ['Too many attempts. Please wait a minute and try again.'];
  const errors = (err.error as { errors?: Record<string, string[]> } | null)?.errors;
  return errors ? Object.values(errors).flat() : ['Something went wrong. Please try again.'];
}

/** The candidate's view of their offer (`/offer/:token`): read it, then accept by typing their name, or decline. */
@Component({
  selector: 'app-offer-response',
  imports: [CurrencyPipe, DatePipe, FormsModule, MatButtonModule, MatCardModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule],
  template: `
    <main class="offer">
      @if (offer.isLoading() && !offer.hasValue()) { <mat-progress-bar mode="indeterminate" /> }
      @if (offer.error()) {
        <h1>Job offer</h1>
        <p class="muted">This link isn't valid. It may have been withdrawn or replaced by a newer email. Please contact the person who sent it.</p>
      }
      @if (offer.hasValue()) {
        @let o = offer.value();
        <header>
          <div class="muted">{{ o.organisation }}</div>
          <h1>Job offer: {{ o.jobTitle }}</h1>
          <div class="muted">For {{ o.candidateName }}</div>
        </header>

        @if (message()) { <p class="done" role="status">{{ message() }}</p> }

        @switch (o.status) {
          @case ('Expired') { <p>This offer expired on {{ o.expiresAt | date: 'd MMMM y, h:mm a' }}. Please contact the team if you're still interested.</p> }
          @case ('Declined') { <p>You declined this offer {{ o.respondedAt | date: 'd MMMM y' }}. Thank you for letting us know.</p> }
          @case ('Accepted') {
            <p class="ok"><mat-icon inline>check_circle</mat-icon> You accepted this offer on {{ o.respondedAt | date: 'd MMMM y, h:mm a' }}, signed "{{ o.signedName }}".</p>
          }
        }

        @if (o.letter) {
          <mat-card appearance="outlined" class="terms">
            <mat-card-content>
              <dl>
                <dt>Salary</dt><dd>{{ o.salary | currency: o.currency! : 'symbol' : '1.0-2' }} {{ per(o.payPeriod!) }}</dd>
                <dt>Start date</dt><dd>{{ o.startDate | date: 'd MMMM y' }}</dd>
                @if (o.status === 'Sent') { <dt>Respond by</dt><dd>{{ o.expiresAt | date: 'd MMMM y, h:mm a' }}</dd> }
              </dl>
            </mat-card-content>
          </mat-card>
          <article class="letter">{{ o.letter }}</article>
          <button mat-stroked-button class="no-print" (click)="print()"><mat-icon>print</mat-icon> Print or save as PDF</button>
        }

        @if (o.status === 'Sent' && !message()) {
          <section class="respond no-print">
            <h2>Your decision</h2>
            @if (!declining()) {
              <p>To accept, type your full name as it appears above. This acts as your signature.</p>
              <mat-form-field appearance="outline" class="full">
                <mat-label>Full name</mat-label>
                <input matInput [(ngModel)]="signedName" name="signedName" autocomplete="name" maxlength="200" />
              </mat-form-field>
              <mat-checkbox [(ngModel)]="agree" name="agree">I accept this offer on the terms above.</mat-checkbox>
              @for (e of errors(); track e) { <p class="error" role="alert">{{ e }}</p> }
              <div class="actions">
                <button mat-flat-button (click)="accept()" [disabled]="busy()">Accept offer</button>
                <button mat-button (click)="declining.set(true); errors.set([])">Decline</button>
              </div>
            } @else {
              <p>We're sorry to hear that. If you'd like, tell us why (optional).</p>
              <mat-form-field appearance="outline" class="full">
                <mat-label>Reason</mat-label>
                <textarea matInput rows="3" [(ngModel)]="reason" name="reason" maxlength="2000"></textarea>
              </mat-form-field>
              @for (e of errors(); track e) { <p class="error" role="alert">{{ e }}</p> }
              <div class="actions">
                <button mat-flat-button class="danger" (click)="decline()" [disabled]="busy()">Decline offer</button>
                <button mat-button (click)="declining.set(false)">Back</button>
              </div>
            }
          </section>
        }
      }
    </main>
  `,
  styles: `
    .offer { max-width: 760px; margin: 0 auto; padding: 32px 16px 64px; }
    header { margin-bottom: 24px; }
    h1 { margin: 4px 0; }
    .terms { margin: 16px 0; }
    dl { display: grid; grid-template-columns: 140px 1fr; gap: 6px 12px; margin: 0; }
    dt { color: var(--mat-sys-on-surface-variant); }
    dd { margin: 0; font-weight: 500; }
    .letter { white-space: pre-wrap; line-height: 1.7; margin: 24px 0; }
    .respond { margin-top: 32px; border-top: 1px solid var(--mat-sys-outline-variant); padding-top: 16px; }
    .full { width: 100%; }
    .actions { display: flex; gap: 8px; margin-top: 16px; }
    .error { color: var(--mat-sys-error); margin: 8px 0 0; }
    .done { padding: 12px 16px; border-radius: 8px; background: var(--mat-sys-surface-container-high); }
    .ok { color: var(--crm-success); }
    @media print { .no-print { display: none !important; } }
  `,
})
export class OfferResponsePage {
  readonly token = input.required<string>();
  private readonly api = inject(CrmApi);
  readonly per = perPeriod;
  readonly offer = httpResource<PublicOffer>(() => `/api/offer-response/${encodeURIComponent(this.token())}`);
  readonly declining = signal(false);
  readonly busy = signal(false);
  readonly errors = signal<string[]>([]);
  readonly message = signal<string | null>(null);
  signedName = '';
  agree = false;
  reason = '';

  accept(): void {
    const problems = [];
    if (!this.signedName.trim()) problems.push('Type your full name to sign.');
    if (!this.agree) problems.push('Tick the box to confirm you accept the offer.');
    this.errors.set(problems);
    if (problems.length) return;
    this.answer(this.api.answerOffer(this.token(), 'accept', { signedName: this.signedName, agree: this.agree }));
  }

  decline(): void {
    this.answer(this.api.answerOffer(this.token(), 'decline', { reason: this.reason.trim() || null }));
  }

  print(): void {
    window.print();
  }

  private answer(request: ReturnType<CrmApi['answerOffer']>): void {
    this.busy.set(true);
    request.subscribe({
      next: (r) => {
        this.busy.set(false);
        this.message.set(r.message);
        this.offer.reload();
      },
      error: (err) => {
        this.busy.set(false);
        this.errors.set(answerErrors(err));
      },
    });
  }
}
