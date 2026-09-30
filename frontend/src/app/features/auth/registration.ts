import { HttpErrorResponse, httpResource } from '@angular/common/http';
import { Component, computed, inject, input, linkedSignal, signal } from '@angular/core';
import { AbstractControl, FormBuilder, FormControl, FormGroup, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { CrmApi } from '../../core/api';
import { RegistrationOptions, VerificationChannel } from '../../core/models';
import { errorMessage } from '../../core/notify.service';
import { PASSWORD_HINT, strongPassword } from './change-password-dialog';

/** Loose client-side check; the server normalises and validates E.164 strictly. */
export const PHONE_PATTERN = /^\+?[0-9\s\-().]{7,20}$/;

const CHANNEL_LABELS: Record<VerificationChannel, string> = { Email: 'Email', Sms: 'Mobile (SMS)' };

function passwordsMatch(group: AbstractControl): ValidationErrors | null {
  return group.get('password')?.value === group.get('confirm')?.value ? null : { mismatch: true };
}

const PAGE_STYLES = `
  .page { min-height: 100vh; display: grid; place-items: center; background: var(--mat-sys-surface-container); padding: 16px; }
  .card { width: min(520px, 94vw); padding-bottom: 8px; }
  .logo { font-size: 40px; width: 40px; height: 40px; color: var(--mat-sys-primary); }
  form, .body { margin-top: 16px; }
  .row { display: grid; grid-template-columns: 1fr 1fr; gap: 0 12px; }
  .full { width: 100%; }
  .error { color: var(--mat-sys-error); margin: 0 0 12px; }
  .done { display: flex; gap: 12px; align-items: flex-start; }
  .done mat-icon { color: var(--mat-sys-primary); }
  .links { margin-top: 12px; text-align: center; }
  @media (max-width: 520px) { .row { grid-template-columns: 1fr; } }
`;

@Component({
  selector: 'app-register',
  imports: [ReactiveFormsModule, RouterLink, MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule, MatProgressBarModule],
  template: `
    <div class="page">
      <mat-card class="card" appearance="outlined">
        @if (busy() || options.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
        <mat-card-header>
          <mat-icon mat-card-avatar class="logo">person_add</mat-icon>
          <mat-card-title>Create your account</mat-card-title>
          <mat-card-subtitle>
            @if (opts()?.available) { Registering with <strong>{{ opts()!.tenantName }}</strong> } @else { Self-registration }
          </mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          @if (!tenantCode()) {
            <form [formGroup]="codeForm" (ngSubmit)="goToCode()" class="body">
              <p>Enter the organisation code your administrator gave you.</p>
              <mat-form-field appearance="outline" class="full">
                <mat-label>Organisation code</mat-label>
                <input matInput formControlName="code" autocomplete="organization" required />
              </mat-form-field>
              <button mat-flat-button class="full" type="submit" [disabled]="code.invalid">Continue</button>
            </form>
          } @else if (done()) {
            <div class="done body">
              <mat-icon>hourglass_top</mat-icon>
              <p>{{ done() }}</p>
            </div>
          } @else if (opts() && !opts()!.available) {
            <p class="body">Self-registration isn't available for this organisation. Ask your administrator for an invitation.</p>
          } @else if (opts()) {
            <form [formGroup]="form" (ngSubmit)="submit()">
              <div class="row">
                <mat-form-field appearance="outline">
                  <mat-label>First name</mat-label>
                  <input matInput formControlName="firstName" autocomplete="given-name" maxlength="100" required />
                </mat-form-field>
                <mat-form-field appearance="outline">
                  <mat-label>Last name</mat-label>
                  <input matInput formControlName="lastName" autocomplete="family-name" maxlength="100" required />
                </mat-form-field>
              </div>
              <mat-form-field appearance="outline" class="full">
                <mat-label>Email</mat-label>
                <input matInput type="email" formControlName="email" autocomplete="email" maxlength="256" required />
                @if (opts()!.emailVerification) { <mat-hint>We'll email you a verification code</mat-hint> }
                @if (form.controls.email.hasError('email')) { <mat-error>Enter a valid email</mat-error> }
              </mat-form-field>
              <mat-form-field appearance="outline" class="full">
                <mat-label>Mobile number{{ opts()!.smsVerification ? '' : ' (optional)' }}</mat-label>
                <input matInput type="tel" formControlName="phoneNumber" autocomplete="tel" placeholder="+61 412 345 678" [required]="opts()!.smsVerification" />
                <mat-hint>International format{{ opts()!.smsVerification ? ". We'll text you a code" : '' }}</mat-hint>
                @if (form.controls.phoneNumber.invalid) { <mat-error>Enter the number with country code, e.g. +61 412 345 678</mat-error> }
              </mat-form-field>
              <mat-form-field appearance="outline" class="full">
                <mat-label>Password</mat-label>
                <input matInput type="password" formControlName="password" autocomplete="new-password" required />
                <mat-hint>{{ hint }}</mat-hint>
                @if (form.controls.password.hasError('weakPassword')) { <mat-error>{{ hint }}</mat-error> }
              </mat-form-field>
              <mat-form-field appearance="outline" class="full">
                <mat-label>Confirm password</mat-label>
                <input matInput type="password" formControlName="confirm" autocomplete="new-password" required />
              </mat-form-field>
              @if (form.hasError('mismatch') && form.controls.confirm.touched) { <p class="error" role="alert">Passwords don't match.</p> }
              @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
              <button mat-flat-button class="full" type="submit" [disabled]="busy()">Register</button>
            </form>
          }
          <div class="links"><a routerLink="/login">Already have an account? Sign in</a></div>
        </mat-card-content>
      </mat-card>
    </div>
  `,
  styles: PAGE_STYLES,
})
export class Register {
  readonly tenantCode = input<string>();
  private readonly api = inject(CrmApi);
  private readonly router = inject(Router);
  readonly hint = PASSWORD_HINT;
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly done = signal<string | null>(null);
  readonly code = new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(64)] });
  readonly codeForm = new FormGroup({ code: this.code });

  readonly options = httpResource<RegistrationOptions>(() =>
    this.tenantCode() ? { url: '/api/auth/registration-options', params: { tenant: this.tenantCode()! } } : undefined,
  );
  readonly opts = computed(() => (this.options.hasValue() ? this.options.value() : undefined));

  readonly form = inject(FormBuilder).nonNullable.group(
    {
      firstName: ['', Validators.required],
      lastName: ['', Validators.required],
      email: ['', [Validators.required, Validators.email]],
      phoneNumber: ['', Validators.pattern(PHONE_PATTERN)],
      password: ['', [Validators.required, strongPassword]],
      confirm: ['', Validators.required],
    },
    { validators: passwordsMatch },
  );

  goToCode(): void {
    if (this.code.invalid) return;
    void this.router.navigate(['/register', this.code.value.trim().toLowerCase()]);
  }

  submit(): void {
    const phone = this.form.controls.phoneNumber;
    if (this.opts()?.smsVerification && !phone.value.trim()) phone.setErrors({ required: true });
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.error.set(null);
    this.api
      .register({
        tenantCode: this.tenantCode()!,
        email: v.email.trim(),
        firstName: v.firstName.trim(),
        lastName: v.lastName.trim(),
        phoneNumber: v.phoneNumber.trim() || null,
        password: v.password,
      })
      .subscribe({
        next: (r) => {
          this.busy.set(false);
          if (r.verification.length === 0) {
            this.done.set(r.message);
            return;
          }
          void this.router.navigate(['/verify'], { queryParams: { email: v.email.trim(), channels: r.verification.join(',') } });
        },
        error: (err: HttpErrorResponse) => {
          this.busy.set(false);
          this.error.set(errorMessage(err));
        },
      });
  }
}

@Component({
  selector: 'app-verify',
  imports: [ReactiveFormsModule, RouterLink, MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule, MatProgressBarModule],
  template: `
    <div class="page">
      <mat-card class="card" appearance="outlined">
        @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
        <mat-card-header>
          <mat-icon mat-card-avatar class="logo">verified_user</mat-icon>
          <mat-card-title>Verify your details</mat-card-title>
          <mat-card-subtitle>{{ email() }}</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          @if (approvalPending()) {
            <div class="done body">
              <mat-icon>hourglass_top</mat-icon>
              <p>Thanks, you're verified. An administrator will review your registration; you can sign in once it's approved.</p>
            </div>
          } @else if (!email()) {
            <p class="body">Open the verification link from the registration page, or sign in to see what still needs verifying.</p>
          } @else {
            @for (channel of remaining(); track channel) {
              <form class="body" [formGroup]="forms[channel]" (ngSubmit)="verify(channel)">
                <mat-form-field appearance="outline" class="full">
                  <mat-label>{{ labels[channel] }} verification code</mat-label>
                  <input matInput formControlName="code" inputmode="numeric" autocomplete="one-time-code" maxlength="10" required />
                  <mat-hint>Sent to your {{ channel === 'Email' ? 'email address' : 'mobile' }}</mat-hint>
                </mat-form-field>
                <button mat-flat-button type="submit" [disabled]="busy() || codes[channel].invalid">Verify</button>
                <button mat-button type="button" (click)="resend(channel)" [disabled]="busy()">Send a new code</button>
              </form>
            }
            @if (message()) { <p class="body">{{ message() }}</p> }
            @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
          }
          <div class="links"><a routerLink="/login">Back to sign in</a></div>
        </mat-card-content>
      </mat-card>
    </div>
  `,
  styles: PAGE_STYLES,
})
export class Verify {
  /** Bound from the query string: ?email=…&channels=Email,Sms */
  readonly email = input<string>();
  readonly channels = input<string>();
  private readonly api = inject(CrmApi);
  readonly labels = CHANNEL_LABELS;
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);
  readonly approvalPending = signal(false);
  readonly remaining = linkedSignal<VerificationChannel[]>(() =>
    (this.channels() ?? 'Email')
      .split(',')
      .filter((c): c is VerificationChannel => c === 'Email' || c === 'Sms'),
  );
  readonly codes: Record<VerificationChannel, FormControl<string>> = {
    Email: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.pattern(/^\d{4,10}$/)] }),
    Sms: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.pattern(/^\d{4,10}$/)] }),
  };
  readonly forms: Record<VerificationChannel, FormGroup> = {
    Email: new FormGroup({ code: this.codes.Email }),
    Sms: new FormGroup({ code: this.codes.Sms }),
  };

  verify(channel: VerificationChannel): void {
    if (this.busy() || this.codes[channel].invalid) return;
    this.busy.set(true);
    this.error.set(null);
    this.message.set(null);
    this.api.verify(this.email()!, channel, this.codes[channel].value.trim()).subscribe({
      next: (r) => {
        this.busy.set(false);
        this.remaining.set(r.remaining);
        if (r.status === 'PendingApproval') this.approvalPending.set(true);
        else this.message.set(`${this.labels[channel]} verified.`);
      },
      error: (err: HttpErrorResponse) => {
        this.busy.set(false);
        this.error.set(errorMessage(err));
      },
    });
  }

  resend(channel: VerificationChannel): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.resendCode(this.email()!, channel).subscribe({
      next: () => {
        this.busy.set(false);
        this.message.set('If a new code is due, we have sent it. Codes can be re-sent after a short wait.');
      },
      error: (err: HttpErrorResponse) => {
        this.busy.set(false);
        this.error.set(errorMessage(err));
      },
    });
  }
}
