import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { CrmApi } from '../../core/api';
import { MetadataService } from '../../core/metadata.service';
import { EmailField, EmailTemplateItem } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { confirm } from '../../shared/confirm-dialog';
import { SettingsBack } from './settings-home';

/** Inserts text into a value at a caret position, returning the new value and caret. */
export function insertAt(value: string, caret: number | null, text: string): { value: string; caret: number } {
  const at = caret === null || caret < 0 || caret > value.length ? value.length : caret;
  return { value: value.slice(0, at) + text + value.slice(at), caret: at + text.length };
}

@Component({
  selector: 'app-email-template-dialog',
  imports: [FormsModule, MatButtonModule, MatChipsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ template ? 'Edit email template' : 'New email template' }}</h2>
    <mat-dialog-content>
      <div class="form">
        <div class="row">
          <mat-form-field appearance="outline" class="grow">
            <mat-label>Name</mat-label>
            <input matInput [(ngModel)]="name" maxlength="200" required />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>For</mat-label>
            <mat-select [(ngModel)]="objectKey" (selectionChange)="loadPlaceholders()">
              @for (o of meta.activeObjects(); track o.key) { <mat-option [value]="o.key">{{ o.pluralLabel }}</mat-option> }
            </mat-select>
          </mat-form-field>
        </div>
        <mat-form-field appearance="outline">
          <mat-label>Subject</mat-label>
          <input matInput [(ngModel)]="subject" maxlength="300" required (blur)="remember('subject', $event)" />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Message</mat-label>
          <textarea matInput rows="12" [(ngModel)]="body" maxlength="10000" required (blur)="remember('body', $event)"></textarea>
        </mat-form-field>
        <p class="muted small">Click a field to insert it where the cursor was. Values are filled in from the record when the email is sent.</p>
        <mat-chip-set aria-label="Placeholders">
          @for (p of placeholders(); track p.key) {
            <mat-chip (click)="insert(p)" [title]="'{{' + p.key + '}}'">{{ p.label }}</mat-chip>
          }
        </mat-chip-set>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="saving() || !name.trim() || !subject.trim() || !body.trim()">Save</button>
    </mat-dialog-actions>
  `,
  styles: `
    .form { display: flex; flex-direction: column; min-width: min(720px, 85vw); padding-top: 8px; }
    .row { display: flex; gap: 8px; flex-wrap: wrap; }
    .grow { flex: 1; }
    .small { font-size: 13px; margin: 0 0 8px; }
    mat-chip { cursor: pointer; }
  `,
})
export class EmailTemplateDialog {
  readonly template = inject<EmailTemplateItem | null>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<EmailTemplateDialog, EmailTemplateItem>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly meta = inject(MetadataService);
  readonly saving = signal(false);
  readonly placeholders = signal<EmailField[]>([]);

  name = this.template?.name ?? '';
  objectKey = this.template?.objectKey ?? this.meta.activeObjects()[0]?.key ?? 'account';
  subject = this.template?.subject ?? '';
  body = this.template?.body ?? '';
  private last: { field: 'subject' | 'body'; caret: number | null } = { field: 'body', caret: null };

  constructor() {
    this.loadPlaceholders();
  }

  loadPlaceholders(): void {
    const key = this.objectKey;
    this.api.emailFields(key).subscribe((f) => key === this.objectKey && this.placeholders.set(f.placeholders));
  }

  remember(field: 'subject' | 'body', event: Event): void {
    this.last = { field, caret: (event.target as HTMLInputElement | HTMLTextAreaElement).selectionStart };
  }

  insert(p: EmailField): void {
    const result = insertAt(this[this.last.field], this.last.caret, `{{${p.key}}}`);
    this[this.last.field] = result.value;
    this.last = { ...this.last, caret: result.caret };
  }

  save(): void {
    this.saving.set(true);
    this.api
      .saveEmailTemplate(this.template?.id ?? null, { name: this.name.trim(), objectKey: this.objectKey, subject: this.subject, body: this.body })
      .subscribe({
        next: (saved) => {
          this.notify.success('Template saved');
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
  selector: 'app-email-templates',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MatTableModule, SettingsBack],
  template: `
    <div class="page-header">
      <div><app-settings-back /><h1>Email templates</h1></div>
      <span class="spacer"></span>
      <button mat-flat-button (click)="open(null)"><mat-icon>add</mat-icon> New template</button>
    </div>
    <p class="muted">Reusable emails, sent from a record's <strong>Email</strong> button or by automation rules. Placeholders such as <code>{{ '{{name}}' }}</code> are filled in from the record.</p>
    <div class="table-wrap">
      @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      <table mat-table [dataSource]="data.value() ?? []">
        <ng-container matColumnDef="name">
          <th mat-header-cell *matHeaderCellDef>Template</th>
          <td mat-cell *matCellDef="let t"><strong>{{ t.name }}</strong><div class="muted">{{ t.subject }}</div></td>
        </ng-container>
        <ng-container matColumnDef="object">
          <th mat-header-cell *matHeaderCellDef>For</th>
          <td mat-cell *matCellDef="let t">{{ meta.label(t.objectKey, true) }}</td>
        </ng-container>
        <ng-container matColumnDef="updated">
          <th mat-header-cell *matHeaderCellDef>Updated</th>
          <td mat-cell *matCellDef="let t">{{ t.updatedAt | date: 'mediumDate' }}</td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let t" class="actions">
            <button mat-icon-button (click)="open(t)" aria-label="Edit"><mat-icon>edit</mat-icon></button>
            <button mat-icon-button (click)="remove(t)" aria-label="Delete"><mat-icon>delete</mat-icon></button>
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
        <tr class="mat-mdc-row" *matNoDataRow><td class="mat-mdc-cell empty" colspan="4">No templates yet.</td></tr>
      </table>
    </div>
  `,
  styles: `td.actions { text-align: right; white-space: nowrap; }`,
})
export class EmailTemplates {
  readonly meta = inject(MetadataService);
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  readonly columns = ['name', 'object', 'updated', 'actions'];
  readonly data = httpResource<EmailTemplateItem[]>(() => '/api/admin/email-templates');

  open(template: EmailTemplateItem | null): void {
    this.dialog.open(EmailTemplateDialog, { data: template, width: '820px', maxHeight: '90vh' }).afterClosed()
      .subscribe((saved) => saved && this.data.reload());
  }

  remove(t: EmailTemplateItem): void {
    confirm(this.dialog, { title: 'Delete template?', message: `"${t.name}" will be deleted.`, confirmText: 'Delete', danger: true }).subscribe((ok) => {
      if (!ok) return;
      this.api.deleteEmailTemplate(t.id).subscribe(() => {
        this.notify.success('Template deleted');
        this.data.reload();
      });
    });
  }
}
