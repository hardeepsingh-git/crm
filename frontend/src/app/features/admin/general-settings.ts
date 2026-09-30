import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { CrmApi } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { MetadataService } from '../../core/metadata.service';
import { NotifyService } from '../../core/notify.service';
import { SettingsBack } from './settings-home';

@Component({
  selector: 'app-general-settings',
  imports: [ReactiveFormsModule, MatButtonModule, MatCardModule, MatFormFieldModule, MatInputModule, SettingsBack],
  template: `
    <div class="page-header"><div><app-settings-back /><h1>Organisation</h1></div></div>
    <mat-card appearance="outlined" class="narrow">
      <mat-card-content>
        <form [formGroup]="form" class="form-grid" (ngSubmit)="save()">
          <mat-form-field appearance="outline" class="span-2">
            <mat-label>Organisation name</mat-label>
            <input matInput formControlName="name" maxlength="200" required />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Currency (ISO 4217)</mat-label>
            <input matInput formControlName="currency" maxlength="3" required placeholder="AUD" />
            <mat-hint>Used for all amounts, e.g. AUD, USD, EUR, INR</mat-hint>
            @if (form.controls.currency.invalid) { <mat-error>Three letters, e.g. AUD</mat-error> }
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Workspace ID</mat-label>
            <input matInput [value]="meta.data()?.tenant?.slug ?? ''" disabled />
            <mat-hint>Template: {{ meta.data()?.tenant?.template }}</mat-hint>
          </mat-form-field>
          <div class="span-2"><button mat-flat-button type="submit" [disabled]="saving()">Save</button></div>
        </form>
      </mat-card-content>
    </mat-card>
  `,
  styles: `.narrow { max-width: 720px; }`,
})
export class GeneralSettings {
  readonly meta = inject(MetadataService);
  private readonly api = inject(CrmApi);
  private readonly auth = inject(AuthService);
  private readonly notify = inject(NotifyService);
  readonly saving = signal(false);

  readonly form = inject(FormBuilder).nonNullable.group({
    name: [this.meta.data()?.tenant.name ?? '', [Validators.required, Validators.maxLength(200)]],
    currency: [this.meta.currency(), [Validators.required, Validators.pattern(/^[A-Za-z]{3}$/)]],
  });

  save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    this.saving.set(true);
    this.api.saveSettings({ name: v.name, currency: v.currency.toUpperCase() }).subscribe({
      next: () => {
        this.saving.set(false);
        this.notify.success('Settings saved');
        void this.meta.load();
        this.auth.reloadUser();
      },
      error: (err) => {
        this.saving.set(false);
        this.notify.formError(err);
      },
    });
  }
}
