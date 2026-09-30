import { Component, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { AuthService } from '../../core/auth.service';
import { NotifyService } from '../../core/notify.service';

export const PASSWORD_HINT = 'At least 10 characters with upper and lower case letters, a digit and a symbol.';

export function strongPassword(control: AbstractControl<string | null>): ValidationErrors | null {
  const v = control.value ?? '';
  const ok = v.length >= 10 && /[a-z]/.test(v) && /[A-Z]/.test(v) && /\d/.test(v) && /[^a-zA-Z0-9]/.test(v);
  return ok ? null : { weakPassword: true };
}

@Component({
  selector: 'app-change-password-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Change password</h2>
    <mat-dialog-content>
      <form [formGroup]="form" id="pwd-form" (ngSubmit)="save()">
        <mat-form-field appearance="outline" class="full">
          <mat-label>Current password</mat-label>
          <input matInput type="password" formControlName="currentPassword" autocomplete="current-password" required />
        </mat-form-field>
        <mat-form-field appearance="outline" class="full">
          <mat-label>New password</mat-label>
          <input matInput type="password" formControlName="newPassword" autocomplete="new-password" required />
          <mat-hint>{{ hint }}</mat-hint>
          @if (form.controls.newPassword.hasError('weakPassword')) {
            <mat-error>{{ hint }}</mat-error>
          }
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="pwd-form" [disabled]="saving()">Change password</button>
    </mat-dialog-actions>
  `,
})
export class ChangePasswordDialog {
  private readonly auth = inject(AuthService);
  private readonly notify = inject(NotifyService);
  private readonly ref = inject(MatDialogRef<ChangePasswordDialog>);
  readonly hint = PASSWORD_HINT;
  readonly saving = signal(false);
  readonly form = inject(FormBuilder).nonNullable.group({
    currentPassword: ['', Validators.required],
    newPassword: ['', [Validators.required, strongPassword]],
  });

  save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.saving.set(true);
    const { currentPassword, newPassword } = this.form.getRawValue();
    this.auth.changePassword(currentPassword, newPassword).subscribe({
      next: () => {
        this.ref.close();
        this.notify.success('Password changed. Please sign in again.');
        this.auth.logout();
      },
      error: (err) => {
        this.saving.set(false);
        this.notify.formError(err);
      },
    });
  }
}
