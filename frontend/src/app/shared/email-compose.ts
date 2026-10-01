import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { CrmApi } from '../core/api';
import { EmailRecipient, EmailTemplateItem } from '../core/models';
import { NotifyService } from '../core/notify.service';

export interface EmailComposeData {
  objectKey: string;
  recordId: string;
}

/** Emails one of the record's own addresses, optionally from a template. The server fills placeholders and logs it on the timeline. */
@Component({
  selector: 'app-email-compose-dialog',
  imports: [FormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Send email</h2>
    <mat-dialog-content>
      @if (loading()) { <mat-progress-bar mode="indeterminate" /> }
      @if (!loading() && !recipients().length) {
        <p class="muted">This record has no email address. Add one (for a candidate, on the candidate record) and try again.</p>
      } @else {
        <div class="form">
          <mat-form-field appearance="outline">
            <mat-label>To</mat-label>
            <mat-select [(ngModel)]="to" required>
              @for (r of recipients(); track r.address) { <mat-option [value]="r.address">{{ r.address }} <span class="muted">({{ r.label }})</span></mat-option> }
            </mat-select>
          </mat-form-field>
          @if (templates().length) {
            <mat-form-field appearance="outline">
              <mat-label>Template</mat-label>
              <mat-select [(ngModel)]="templateId" (selectionChange)="applyTemplate()">
                <mat-option [value]="null">No template</mat-option>
                @for (t of templates(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }
              </mat-select>
            </mat-form-field>
          }
          <mat-form-field appearance="outline">
            <mat-label>Subject</mat-label>
            <input matInput [(ngModel)]="subject" maxlength="300" required />
          </mat-form-field>
          @if (blankFields().length) {
            <p class="warning"><mat-icon inline>warning</mat-icon> Not filled in on this record: {{ blankFields().join(', ') }}. Edit the text below or update the record first.</p>
          }
          <mat-form-field appearance="outline">
            <mat-label>Message</mat-label>
            <textarea matInput rows="12" [(ngModel)]="body" maxlength="10000" required></textarea>
            <mat-hint>Replies go to your email address. The email is logged on this record's timeline.</mat-hint>
          </mat-form-field>
        </div>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="send()" [disabled]="sending() || !to || !subject.trim() || !body.trim()"><mat-icon>send</mat-icon> Send</button>
    </mat-dialog-actions>
  `,
  styles: `
    .form { display: flex; flex-direction: column; min-width: min(560px, 80vw); padding-top: 8px; }
    textarea { font-family: inherit; }
    .warning { background: var(--mat-sys-error-container); color: var(--mat-sys-on-error-container); padding: 8px 12px; border-radius: 8px; margin: 0 0 16px; }
  `,
})
export class EmailComposeDialog {
  private readonly data = inject<EmailComposeData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<EmailComposeDialog, boolean>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly loading = signal(true);
  readonly sending = signal(false);
  readonly recipients = signal<EmailRecipient[]>([]);
  readonly templates = signal<EmailTemplateItem[]>([]);
  readonly blankFields = signal<string[]>([]);

  to = '';
  templateId: string | null = null;
  subject = '';
  body = '';

  constructor() {
    this.api.composeEmail(this.data.objectKey, this.data.recordId).subscribe({
      next: (c) => {
        this.recipients.set(c.recipients);
        this.to = c.recipients[0]?.address ?? '';
        this.loading.set(false);
      },
      error: () => this.ref.close(false),
    });
    this.api.emailTemplatesFor(this.data.objectKey).subscribe((t) => this.templates.set(t));
  }

  applyTemplate(): void {
    if (!this.templateId) {
      this.blankFields.set([]);
      return;
    }
    this.api.composeEmail(this.data.objectKey, this.data.recordId, this.templateId).subscribe((c) => {
      this.subject = c.subject;
      this.body = c.body;
      this.blankFields.set(c.blankFields ?? []);
    });
  }

  send(): void {
    this.sending.set(true);
    const { objectKey, recordId } = this.data;
    this.api.sendEmail({ objectKey, recordId, to: this.to, subject: this.subject, body: this.body, templateId: this.templateId }).subscribe({
      next: () => {
        this.notify.success(`Email to ${this.to} is on its way`);
        this.ref.close(true);
      },
      error: (err) => {
        this.sending.set(false);
        this.notify.formError(err);
      },
    });
  }
}
