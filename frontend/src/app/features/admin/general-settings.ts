import { httpResource } from '@angular/common/http';
import { Component, computed, inject, linkedSignal, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { CrmApi } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { MetadataService } from '../../core/metadata.service';
import { OrganisationSettings } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SettingsBack } from './settings-home';

@Component({
  selector: 'app-general-settings',
  imports: [ReactiveFormsModule, MatButtonModule, MatCardModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSlideToggleModule, SettingsBack],
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
            <mat-label>Organisation code</mat-label>
            <input matInput [value]="meta.data()?.tenant?.slug ?? ''" disabled />
            <mat-hint>Workspace ID used in links. Template: {{ meta.data()?.tenant?.template }}</mat-hint>
          </mat-form-field>
          <div class="span-2 section">
            <h3>Self-registration</h3>
            <mat-slide-toggle [checked]="selfRegistration()" (change)="selfRegistration.set($event.checked)">
              Let people request an account themselves
            </mat-slide-toggle>
            <p class="muted">
              New registrations can't sign in until someone with <em>Manage users</em> approves them and assigns a role or group
              (Settings &rsaquo; Users &rsaquo; Awaiting approval).
            </p>
            @if (org(); as o) {
              @if (!o.selfRegistrationAllowedByPlatform) {
                <p class="warn">Self-registration is currently switched off for the whole platform, so this setting has no effect until a platform administrator enables it.</p>
              } @else if (selfRegistration()) {
                <p>Share this link: <a [href]="registrationLink()" target="_blank" rel="noopener">{{ registrationLink() }}</a>
                  <button mat-icon-button type="button" (click)="copyLink()" aria-label="Copy link"><mat-icon>content_copy</mat-icon></button></p>
                <p class="muted">Verification required: {{ verificationSummary(o) }}</p>
              }
            }
          </div>
          @if (org()?.careersAvailable) {
            <div class="span-2 section">
              <h3>Careers page</h3>
              <mat-slide-toggle [checked]="careers()" (change)="careers.set($event.checked)">Publish a public careers page</mat-slide-toggle>
              <p class="muted">
                Lists open job openings that have <em>Show on careers page</em> ticked. Candidates apply with their CV; each application
                appears in the job's pipeline and the job's owner is notified.
              </p>
              <mat-form-field appearance="outline" class="full">
                <mat-label>Introduction</mat-label>
                <textarea matInput rows="3" maxlength="2000" [value]="careersIntro()" (input)="careersIntro.set(textValue($event))"
                  placeholder="Who you are and why people love working with you"></textarea>
              </mat-form-field>
              @if (careers()) {
                <p>Your careers page: <a [href]="careersLink()" target="_blank" rel="noopener">{{ careersLink() }}</a>
                  <button mat-icon-button type="button" (click)="copy(careersLink())" aria-label="Copy careers link"><mat-icon>content_copy</mat-icon></button></p>
              }
            </div>
          }
          <div class="span-2"><button mat-flat-button type="submit" [disabled]="saving()">Save</button></div>
        </form>
      </mat-card-content>
    </mat-card>
  `,
  styles: `
    .narrow { max-width: 720px; }
    .section h3 { margin: 8px 0; }
    .warn { color: var(--mat-sys-error); }
    .full { width: 100%; }
  `,
})
export class GeneralSettings {
  readonly meta = inject(MetadataService);
  private readonly api = inject(CrmApi);
  private readonly auth = inject(AuthService);
  private readonly notify = inject(NotifyService);
  readonly saving = signal(false);
  readonly settings = httpResource<OrganisationSettings>(() => '/api/settings');
  readonly org = computed(() => (this.settings.hasValue() ? this.settings.value() : undefined));
  readonly selfRegistration = linkedSignal(() => this.org()?.selfRegistrationEnabled ?? false);
  readonly registrationLink = computed(() => `${location.origin}/register/${this.meta.data()?.tenant.slug ?? ''}`);
  readonly careers = linkedSignal(() => this.org()?.careersEnabled ?? false);
  readonly careersIntro = linkedSignal(() => this.org()?.careersIntro ?? '');
  readonly careersLink = computed(() => `${location.origin}/careers/${this.meta.data()?.tenant.slug ?? ''}`);

  textValue(event: Event): string {
    return (event.target as HTMLTextAreaElement).value;
  }

  copy(text: string): void {
    void navigator.clipboard?.writeText(text).then(() => this.notify.success('Link copied'));
  }

  verificationSummary(o: OrganisationSettings): string {
    const parts = [o.emailVerification ? 'email' : '', o.smsVerification ? 'mobile (SMS)' : ''].filter(Boolean);
    return parts.length ? parts.join(' and ') : 'none (administrator approval only)';
  }

  copyLink(): void {
    void navigator.clipboard?.writeText(this.registrationLink()).then(() => this.notify.success('Link copied'));
  }

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
    this.api.saveSettings({
      name: v.name,
      currency: v.currency.toUpperCase(),
      selfRegistrationEnabled: this.selfRegistration(),
      ...(this.org()?.careersAvailable ? { careersEnabled: this.careers(), careersIntro: this.careersIntro() } : {}),
    }).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.settings.set(saved);
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
