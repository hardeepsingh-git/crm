import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { errorMessage } from '../../core/notify.service';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule, MatProgressBarModule],
  template: `
    <div class="login-page">
      <mat-card class="login-card" appearance="outlined">
        @if (busy()) {
          <mat-progress-bar mode="indeterminate" />
        }
        <mat-card-header>
          <mat-icon mat-card-avatar class="logo">hub</mat-icon>
          <mat-card-title>CRM</mat-card-title>
          <mat-card-subtitle>Sign in to continue</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          <form [formGroup]="form" (ngSubmit)="submit()">
            <mat-form-field appearance="outline" class="full">
              <mat-label>Email</mat-label>
              <input matInput type="email" formControlName="email" autocomplete="username" required />
            </mat-form-field>
            <mat-form-field appearance="outline" class="full">
              <mat-label>Password</mat-label>
              <input matInput [type]="showPassword() ? 'text' : 'password'" formControlName="password" autocomplete="current-password" required />
              <button mat-icon-button matSuffix type="button" (click)="showPassword.set(!showPassword())"
                [attr.aria-label]="showPassword() ? 'Hide password' : 'Show password'">
                <mat-icon>{{ showPassword() ? 'visibility_off' : 'visibility' }}</mat-icon>
              </button>
            </mat-form-field>
            @if (error()) {
              <p class="error" role="alert">{{ error() }}</p>
            }
            <button mat-flat-button class="full" type="submit" [disabled]="busy()">Sign in</button>
          </form>
        </mat-card-content>
      </mat-card>
    </div>
  `,
  styles: `
    .login-page { min-height: 100vh; display: grid; place-items: center; background: var(--mat-sys-surface-container); }
    .login-card { width: min(400px, 92vw); padding-bottom: 8px; }
    .logo { font-size: 40px; width: 40px; height: 40px; color: var(--mat-sys-primary); }
    form { margin-top: 16px; }
    .error { color: var(--mat-sys-error); margin: 0 0 12px; }
  `,
})
export class Login {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly showPassword = signal(false);
  readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });

  submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    const { email, password } = this.form.getRawValue();
    this.auth.login(email, password).subscribe({
      next: () => {
        const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
        // Only allow in-app redirects to avoid open-redirects.
        void this.router.navigateByUrl(returnUrl?.startsWith('/') && !returnUrl.startsWith('//') ? returnUrl : '/dashboard');
      },
      error: (err) => {
        this.busy.set(false);
        this.error.set(errorMessage(err));
      },
    });
  }
}
