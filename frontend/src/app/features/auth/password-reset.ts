import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, input, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { CrmApi } from '../../core/api';
import { errorMessage } from '../../core/notify.service';
import { PASSWORD_HINT, strongPassword } from './change-password-dialog';

const IMPORTS = [ReactiveFormsModule, RouterLink, MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule, MatProgressBarModule];

const STYLES = `
  .page { min-height: 100vh; display: grid; place-items: center; background: var(--mat-sys-surface-container); padding: 16px; }
  .card { width: min(420px, 94vw); padding-bottom: 8px; }
  .logo { font-size: 40px; width: 40px; height: 40px; color: var(--mat-sys-primary); }
  form, .body { margin-top: 16px; }
  .full { width: 100%; }
  .error { color: var(--mat-sys-error); margin: 0 0 12px; }
  .done { display: flex; gap: 12px; align-items: flex-start; }
  .done mat-icon { color: var(--mat-sys-primary); }
  .links { margin-top: 12px; text-align: center; }
`;

function passwordsMatch(group: AbstractControl): ValidationErrors | null {
  return group.get('password')?.value === group.get('confirm')?.value ? null : { mismatch: true };
}

@Component({
  selector: 'app-forgot-password',
  imports: IMPORTS,
  template: `
    <div class="page">
      <mat-card class="card" appearance="outlined">
        @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
        <mat-card-header>
          <mat-icon mat-card-avatar class="logo">lock_reset</mat-icon>
          <mat-card-title>Forgot your password?</mat-card-title>
          <mat-card-subtitle>We'll email you a link to choose a new one</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          @if (sent()) {
            <div class="done body">
              <mat-icon>mark_email_read</mat-icon>
              <p>If an account exists for <strong>{{ form.controls.email.value }}</strong>, we've sent a reset link. It expires in an hour. Check your spam folder if it doesn't arrive.</p>
            </div>
          } @else {
            <form [formGroup]="form" (ngSubmit)="submit()">
              <mat-form-field appearance="outline" class="full">
                <mat-label>Email</mat-label>
                <input matInput type="email" formControlName="email" autocomplete="username" maxlength="256" required />
                @if (form.controls.email.hasError('email')) { <mat-error>Enter a valid email</mat-error> }
              </mat-form-field>
              @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
              <button mat-flat-button class="full" type="submit" [disabled]="busy()">Send reset link</button>
            </form>
          }
          <div class="links"><a routerLink="/login">Back to sign in</a></div>
        </mat-card-content>
      </mat-card>
    </div>
  `,
  styles: STYLES,
})
export class ForgotPassword {
  private readonly api = inject(CrmApi);
  readonly busy = signal(false);
  readonly sent = signal(false);
  readonly error = signal<string | null>(null);
  readonly form = inject(FormBuilder).nonNullable.group({ email: ['', [Validators.required, Validators.email]] });

  submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.api.forgotPassword(this.form.controls.email.value.trim()).subscribe({
      next: () => {
        this.busy.set(false);
        this.sent.set(true);
      },
      error: (err: HttpErrorResponse) => {
        this.busy.set(false);
        this.error.set(errorMessage(err));
      },
    });
  }
}

@Component({
  selector: 'app-reset-password',
  imports: IMPORTS,
  template: `
    <div class="page">
      <mat-card class="card" appearance="outlined">
        @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
        <mat-card-header>
          <mat-icon mat-card-avatar class="logo">password</mat-icon>
          <mat-card-title>Choose a new password</mat-card-title>
        </mat-card-header>
        <mat-card-content>
          @if (done()) {
            <div class="done body">
              <mat-icon>check_circle</mat-icon>
              <p>Your password has been changed and you've been signed out on all devices. <a routerLink="/login">Sign in</a> with your new password.</p>
            </div>
          } @else if (!token()) {
            <p class="body">This link is incomplete. Open the link from your email again, or <a routerLink="/forgot-password">request a new one</a>.</p>
          } @else {
            <form [formGroup]="form" (ngSubmit)="submit()">
              <mat-form-field appearance="outline" class="full">
                <mat-label>New password</mat-label>
                <input matInput type="password" formControlName="password" autocomplete="new-password" required />
                <mat-hint>{{ hint }}</mat-hint>
                @if (form.controls.password.hasError('weakPassword')) { <mat-error>{{ hint }}</mat-error> }
              </mat-form-field>
              <mat-form-field appearance="outline" class="full">
                <mat-label>Confirm new password</mat-label>
                <input matInput type="password" formControlName="confirm" autocomplete="new-password" required />
              </mat-form-field>
              @if (form.hasError('mismatch') && form.controls.confirm.touched) { <p class="error" role="alert">Passwords don't match.</p> }
              @if (error()) {
                <p class="error" role="alert">{{ error() }}</p>
                <p><a routerLink="/forgot-password">Request a new link</a></p>
              }
              <button mat-flat-button class="full" type="submit" [disabled]="busy()">Change password</button>
            </form>
          }
          <div class="links"><a routerLink="/login">Back to sign in</a></div>
        </mat-card-content>
      </mat-card>
    </div>
  `,
  styles: STYLES,
})
export class ResetPassword {
  /** Bound from the emailed link: /reset-password?token=… */
  readonly token = input<string>();
  private readonly api = inject(CrmApi);
  readonly hint = PASSWORD_HINT;
  readonly busy = signal(false);
  readonly done = signal(false);
  readonly error = signal<string | null>(null);
  readonly form = inject(FormBuilder).nonNullable.group(
    { password: ['', [Validators.required, strongPassword]], confirm: ['', Validators.required] },
    { validators: passwordsMatch },
  );

  submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.api.resetPasswordWithToken(this.token()!, this.form.controls.password.value).subscribe({
      next: () => {
        this.busy.set(false);
        this.done.set(true);
      },
      error: (err: HttpErrorResponse) => {
        this.busy.set(false);
        this.error.set(errorMessage(err));
      },
    });
  }
}
