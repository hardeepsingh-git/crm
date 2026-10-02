import { httpResource } from '@angular/common/http';
import { Component, effect, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { CrmApi } from '../../core/api';
import { AiProvider, EmailProvider, PlatformSettings, PlatformSettingsView, SmsProvider, VerificationChannel } from '../../core/models';
import { NotifyService } from '../../core/notify.service';

/** Platform-wide feature flags and messaging providers. Secrets stay in server configuration and are only reported as set/not set. */
@Component({
  selector: 'app-platform-settings',
  imports: [ReactiveFormsModule, MatButtonModule, MatCardModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatSlideToggleModule],
  template: `
    <div class="page-header">
      <div><h1>Platform settings</h1><div class="muted">Apply to every organisation on this platform.</div></div>
      <span class="spacer"></span>
      <button mat-flat-button (click)="save()" [disabled]="saving() || !view.hasValue()">Save</button>
    </div>

    <form [formGroup]="form" class="cards">
      <mat-card appearance="outlined">
        <mat-card-header><mat-card-title>Self-registration</mat-card-title></mat-card-header>
        <mat-card-content>
          <mat-slide-toggle formControlName="selfRegistrationEnabled">Allow self-registration</mat-slide-toggle>
          <p class="muted">Master switch. Each organisation must also opt in (Settings &rsaquo; Organisation). Registrations always need administrator approval.</p>
          <mat-slide-toggle formControlName="emailVerificationEnabled">Require email verification</mat-slide-toggle>
          <p class="muted">Sends a one-time code to the registrant's email address.</p>
          <mat-slide-toggle formControlName="smsVerificationEnabled">Require mobile (SMS) verification</mat-slide-toggle>
          <p class="muted">Makes a mobile number mandatory and sends a one-time code by SMS.</p>
          @if (form.controls.emailVerificationEnabled.value && form.controls.emailProvider.value === 'Log') {
            <p class="warn">No email provider is configured yet, so codes are only written to the API log (Development). Configure SMTP below before relying on it.</p>
          }
          @if (form.controls.smsVerificationEnabled.value && form.controls.smsProvider.value === 'Log') {
            <p class="warn">No SMS gateway is configured yet, so codes are only written to the API log (Development). Configure the gateway below before relying on it.</p>
          }
        </mat-card-content>
      </mat-card>

      <mat-card appearance="outlined">
        <mat-card-header><mat-card-title>Verification codes</mat-card-title></mat-card-header>
        <mat-card-content class="form-grid">
          <mat-form-field appearance="outline"><mat-label>Code length</mat-label><input matInput type="number" min="4" max="10" formControlName="codeLength" /></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Valid for (minutes)</mat-label><input matInput type="number" min="1" max="60" formControlName="codeTtlMinutes" /></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Attempts per code</mat-label><input matInput type="number" min="1" max="10" formControlName="maxAttempts" /></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Resend wait (seconds)</mat-label><input matInput type="number" min="0" max="600" formControlName="resendCooldownSeconds" /></mat-form-field>
        </mat-card-content>
      </mat-card>

      <mat-card appearance="outlined">
        <mat-card-header><mat-card-title>Email provider</mat-card-title></mat-card-header>
        <mat-card-content class="form-grid">
          <mat-form-field appearance="outline" class="span-2">
            <mat-label>Provider</mat-label>
            <mat-select formControlName="emailProvider">
              <mat-option value="Log">Not configured (log only)</mat-option>
              <mat-option value="Smtp">SMTP server</mat-option>
            </mat-select>
          </mat-form-field>
          @if (form.controls.emailProvider.value === 'Smtp') {
            <mat-form-field appearance="outline"><mat-label>SMTP host</mat-label><input matInput formControlName="smtpHost" placeholder="smtp.example.com" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Port</mat-label><input matInput type="number" formControlName="smtpPort" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Username (optional)</mat-label><input matInput formControlName="smtpUsername" autocomplete="off" /></mat-form-field>
            <div class="toggle"><mat-slide-toggle formControlName="smtpUseStartTls">Use TLS (STARTTLS)</mat-slide-toggle></div>
            <mat-form-field appearance="outline"><mat-label>From address</mat-label><input matInput type="email" formControlName="emailFromAddress" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>From name</mat-label><input matInput formControlName="emailFromName" /></mat-form-field>
            <p class="span-2 muted">
              <mat-icon inline>{{ view.value()?.smtpPasswordConfigured ? 'check_circle' : 'info' }}</mat-icon>
              SMTP password {{ view.value()?.smtpPasswordConfigured ? 'is set' : 'is not set' }} in server configuration
              (<code>Email:SmtpPassword</code> in user-secrets or the <code>Email__SmtpPassword</code> environment variable). It is never stored in the database.
            </p>
          }
          <div class="span-2 test">
            <mat-form-field appearance="outline" subscriptSizing="dynamic"><mat-label>Send a test email to</mat-label><input matInput #emailTo type="email" /></mat-form-field>
            <button mat-stroked-button type="button" (click)="test('Email', emailTo.value)" [disabled]="testing()">Send test</button>
          </div>
        </mat-card-content>
      </mat-card>

      <mat-card appearance="outlined">
        <mat-card-header><mat-card-title>SMS gateway</mat-card-title></mat-card-header>
        <mat-card-content class="form-grid">
          <mat-form-field appearance="outline" class="span-2">
            <mat-label>Provider</mat-label>
            <mat-select formControlName="smsProvider">
              <mat-option value="Log">Not configured (log only)</mat-option>
              <mat-option value="Http">HTTP gateway (JSON)</mat-option>
            </mat-select>
          </mat-form-field>
          @if (form.controls.smsProvider.value === 'Http') {
            <mat-form-field appearance="outline" class="span-2">
              <mat-label>Gateway URL</mat-label><input matInput formControlName="smsGatewayUrl" placeholder="https://sms.internal/api/send" />
              <mat-hint>Receives POST {{ '{' }} "to", "from", "message" {{ '}' }}</mat-hint>
            </mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Sender ID</mat-label><input matInput formControlName="smsSenderId" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Auth header</mat-label><input matInput formControlName="smsAuthHeader" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Auth scheme</mat-label><input matInput formControlName="smsAuthScheme" /><mat-hint>e.g. Bearer; leave empty to send the key as-is</mat-hint></mat-form-field>
            <p class="span-2 muted">
              <mat-icon inline>{{ view.value()?.smsApiKeyConfigured ? 'check_circle' : 'info' }}</mat-icon>
              API key {{ view.value()?.smsApiKeyConfigured ? 'is set' : 'is not set' }} in server configuration
              (<code>Sms:ApiKey</code> / <code>Sms__ApiKey</code>). It is never stored in the database.
            </p>
          }
          <div class="span-2 test">
            <mat-form-field appearance="outline" subscriptSizing="dynamic"><mat-label>Send a test SMS to</mat-label><input matInput #smsTo type="tel" placeholder="+61412345678" /></mat-form-field>
            <button mat-stroked-button type="button" (click)="test('Sms', smsTo.value)" [disabled]="testing()">Send test</button>
          </div>
        </mat-card-content>
      </mat-card>

      <mat-card appearance="outlined">
        <mat-card-header><mat-card-title>AI provider (mock interview feedback)</mat-card-title></mat-card-header>
        <mat-card-content class="form-grid">
          <p class="span-2 muted">
            <mat-icon inline>{{ view.value()?.aiServerEnabled ? 'check_circle' : 'block' }}</mat-icon>
            AI feedback is {{ view.value()?.aiServerEnabled ? 'allowed' : 'switched off' }} on this server (<code>Ai:Enabled</code>).
            Nothing is sent to a model unless this is on, a provider is set here, and an organisation opts in.
          </p>
          <mat-form-field appearance="outline" class="span-2">
            <mat-label>Provider</mat-label>
            <mat-select formControlName="aiProvider">
              <mat-option value="None">Not configured</mat-option>
              <mat-option value="OpenAI">OpenAI or compatible (Chat Completions)</mat-option>
              <mat-option value="AzureOpenAI">Azure OpenAI</mat-option>
            </mat-select>
          </mat-form-field>
          @if (form.controls.aiProvider.value !== 'None') {
            <mat-form-field appearance="outline" class="span-2">
              <mat-label>Endpoint</mat-label>
              <input matInput formControlName="aiEndpoint" [placeholder]="form.controls.aiProvider.value === 'AzureOpenAI' ? 'https://my-resource.openai.azure.com' : 'https://api.openai.com/v1'" />
              <mat-hint>HTTPS only</mat-hint>
            </mat-form-field>
            <mat-form-field appearance="outline"><mat-label>{{ form.controls.aiProvider.value === 'AzureOpenAI' ? 'Deployment' : 'Model' }}</mat-label><input matInput formControlName="aiModel" /></mat-form-field>
            @if (form.controls.aiProvider.value === 'AzureOpenAI') {
              <mat-form-field appearance="outline"><mat-label>API version</mat-label><input matInput formControlName="aiApiVersion" placeholder="2024-10-21" /></mat-form-field>
            }
            <mat-form-field appearance="outline"><mat-label>Key name</mat-label><input matInput formControlName="aiApiKeyName" /><mat-hint>Reads Ai:Keys:&lt;name&gt;</mat-hint></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Timeout (seconds)</mat-label><input matInput type="number" min="5" max="120" formControlName="aiTimeoutSeconds" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Retries</mat-label><input matInput type="number" min="0" max="5" formControlName="aiMaxRetries" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Max output tokens</mat-label><input matInput type="number" min="200" max="4000" formControlName="aiMaxOutputTokens" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Temperature</mat-label><input matInput type="number" min="0" max="1" step="0.1" formControlName="aiTemperature" /><mat-hint>Low = consistent scoring</mat-hint></mat-form-field>
            <p class="span-2 muted">
              <mat-icon inline>{{ view.value()?.aiKeyConfigured ? 'check_circle' : 'info' }}</mat-icon>
              API key {{ view.value()?.aiKeyConfigured ? 'is set' : 'is not set' }} in server configuration
              (<code>Ai:Keys:{{ form.controls.aiApiKeyName.value || 'Default' }}</code>). It is never stored in the database.
            </p>
          }
        </mat-card-content>
      </mat-card>
    </form>
  `,
  styles: `
    .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(420px, 1fr)); gap: 16px; }
    mat-slide-toggle { display: block; margin-top: 8px; }
    .muted { margin: 4px 0 12px; }
    .warn { color: var(--mat-sys-error); }
    .toggle { display: flex; align-items: center; }
    .test { display: flex; gap: 8px; align-items: center; }
    .test mat-form-field { flex: 1; }
  `,
})
export class PlatformSettingsPage {
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly saving = signal(false);
  readonly testing = signal(false);
  readonly view = httpResource<PlatformSettingsView>(() => '/api/platform/settings');

  readonly form = inject(FormBuilder).nonNullable.group({
    selfRegistrationEnabled: [false],
    emailVerificationEnabled: [false],
    smsVerificationEnabled: [false],
    codeLength: [6, [Validators.required, Validators.min(4), Validators.max(10)]],
    codeTtlMinutes: [10, [Validators.required, Validators.min(1), Validators.max(60)]],
    maxAttempts: [5, [Validators.required, Validators.min(1), Validators.max(10)]],
    resendCooldownSeconds: [60, [Validators.required, Validators.min(0), Validators.max(600)]],
    emailProvider: ['Log' as EmailProvider],
    smtpHost: [''],
    smtpPort: [587, [Validators.min(1), Validators.max(65535)]],
    smtpUsername: [''],
    smtpUseStartTls: [true],
    emailFromAddress: ['', Validators.email],
    emailFromName: ['CRM'],
    smsProvider: ['Log' as SmsProvider],
    smsGatewayUrl: [''],
    smsSenderId: [''],
    smsAuthHeader: ['Authorization'],
    smsAuthScheme: ['Bearer'],
    aiProvider: ['None' as AiProvider],
    aiEndpoint: [''],
    aiModel: [''],
    aiApiVersion: [''],
    aiApiKeyName: ['Default', [Validators.required, Validators.pattern(/^[A-Za-z0-9_-]{1,64}$/)]],
    aiTimeoutSeconds: [30, [Validators.min(5), Validators.max(120)]],
    aiMaxRetries: [2, [Validators.min(0), Validators.max(5)]],
    aiMaxOutputTokens: [1200, [Validators.min(200), Validators.max(4000)]],
    aiTemperature: [0.2, [Validators.min(0), Validators.max(1)]],
  });

  constructor() {
    effect(() => {
      const v = this.view.value();
      if (v) this.form.reset(this.toForm(v.settings));
    });
  }

  save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const blankToNull = (s: string) => (s.trim() ? s.trim() : null);
    const settings: PlatformSettings = {
      ...v,
      codeLength: Number(v.codeLength),
      codeTtlMinutes: Number(v.codeTtlMinutes),
      maxAttempts: Number(v.maxAttempts),
      resendCooldownSeconds: Number(v.resendCooldownSeconds),
      smtpPort: Number(v.smtpPort),
      smtpHost: blankToNull(v.smtpHost),
      smtpUsername: blankToNull(v.smtpUsername),
      emailFromAddress: blankToNull(v.emailFromAddress),
      smsGatewayUrl: blankToNull(v.smsGatewayUrl),
      smsSenderId: blankToNull(v.smsSenderId),
      smsAuthScheme: v.smsAuthScheme.trim(),
      aiEndpoint: blankToNull(v.aiEndpoint),
      aiModel: blankToNull(v.aiModel),
      aiApiVersion: blankToNull(v.aiApiVersion),
      aiApiKeyName: v.aiApiKeyName.trim(),
      aiTimeoutSeconds: Number(v.aiTimeoutSeconds),
      aiMaxRetries: Number(v.aiMaxRetries),
      aiMaxOutputTokens: Number(v.aiMaxOutputTokens),
      aiTemperature: Number(v.aiTemperature),
    };
    this.saving.set(true);
    this.api.savePlatformSettings(settings).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.view.set(saved);
        this.notify.success('Platform settings saved');
      },
      error: (err) => {
        this.saving.set(false);
        this.notify.formError(err);
      },
    });
  }

  test(channel: VerificationChannel, to: string): void {
    if (!to.trim()) {
      this.notify.warn(`Enter ${channel === 'Email' ? 'an email address' : 'a mobile number'} to send the test to.`);
      return;
    }
    this.testing.set(true);
    this.api.testMessage(channel, to.trim()).subscribe({
      next: () => {
        this.testing.set(false);
        this.notify.success('Test sent. Save your changes first if you edited the provider (tests use the saved settings).');
      },
      error: (err) => {
        this.testing.set(false);
        this.notify.error(err);
      },
    });
  }

  private toForm(s: PlatformSettings) {
    return { ...s, smtpHost: s.smtpHost ?? '', smtpUsername: s.smtpUsername ?? '', emailFromAddress: s.emailFromAddress ?? '',
      smsGatewayUrl: s.smsGatewayUrl ?? '', smsSenderId: s.smsSenderId ?? '', aiProvider: s.aiProvider ?? 'None', aiEndpoint: s.aiEndpoint ?? '',
      aiModel: s.aiModel ?? '', aiApiVersion: s.aiApiVersion ?? '', aiApiKeyName: s.aiApiKeyName ?? 'Default', aiTimeoutSeconds: s.aiTimeoutSeconds ?? 30,
      aiMaxRetries: s.aiMaxRetries ?? 2, aiMaxOutputTokens: s.aiMaxOutputTokens ?? 1200, aiTemperature: s.aiTemperature ?? 0.2 };
  }
}
