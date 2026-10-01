import { DatePipe } from '@angular/common';
import { HttpErrorResponse, httpResource } from '@angular/common/http';
import { Component, computed, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { CrmApi } from '../core/api';
import { AuthService } from '../core/auth.service';
import { AttachmentItem } from '../core/models';
import { NotifyService, errorMessage } from '../core/notify.service';
import { confirm } from './confirm-dialog';

/** Mirrors the API allow-list; the server enforces it. */
export const ATTACHMENT_EXTENSIONS = ['.pdf', '.doc', '.docx', '.rtf', '.odt', '.txt', '.csv', '.xls', '.xlsx', '.ppt', '.pptx', '.png', '.jpg', '.jpeg', '.gif', '.webp'];
export const MAX_ATTACHMENT_MB = 10;

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

/** Client-side pre-check so users get instant feedback; returns an error message or null. */
export function checkAttachment(file: File): string | null {
  const dot = file.name.lastIndexOf('.');
  const ext = dot >= 0 ? file.name.slice(dot).toLowerCase() : '';
  if (!ATTACHMENT_EXTENSIONS.includes(ext)) return `${file.name}: this file type isn't allowed.`;
  if (file.size === 0) return `${file.name} is empty.`;
  if (file.size > MAX_ATTACHMENT_MB * 1024 * 1024) return `${file.name} is larger than ${MAX_ATTACHMENT_MB} MB.`;
  return null;
}

function fileIcon(contentType: string): string {
  if (contentType.startsWith('image/')) return 'image';
  if (contentType === 'application/pdf') return 'picture_as_pdf';
  if (contentType.includes('sheet') || contentType.includes('excel') || contentType === 'text/csv') return 'table_chart';
  return 'description';
}

@Component({
  selector: 'app-attachments-panel',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule],
  template: `
    <div class="head">
      <h3><mat-icon inline>attach_file</mat-icon> Files @if (items().length) { <span class="muted">({{ items().length }})</span> }</h3>
      <span class="spacer"></span>
      @if (canEdit()) {
        <input #picker type="file" hidden multiple [accept]="accept" (change)="upload(picker)" />
        <button mat-stroked-button (click)="picker.click()" [disabled]="busy()"><mat-icon>upload</mat-icon> Upload</button>
      }
    </div>
    @if (busy() || files.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @for (f of items(); track f.id) {
      <div class="file">
        <mat-icon class="type">{{ icon(f.contentType) }}</mat-icon>
        <div class="meta">
          <button class="name" type="button" (click)="download(f)" [title]="'Download ' + f.fileName">{{ f.fileName }}</button>
          <div class="muted small">{{ size(f.size) }} &middot; {{ f.uploadedByName || 'Unknown' }} &middot; {{ f.createdAt | date: 'medium' }}</div>
        </div>
        <button mat-icon-button (click)="download(f)" matTooltip="Download" [attr.aria-label]="'Download ' + f.fileName"><mat-icon>download</mat-icon></button>
        @if (canEdit()) {
          <button mat-icon-button (click)="remove(f)" matTooltip="Delete" [attr.aria-label]="'Delete ' + f.fileName"><mat-icon>delete</mat-icon></button>
        }
      </div>
    } @empty {
      @if (!files.isLoading()) {
        <p class="muted small">No files yet.@if (canEdit()) { Upload résumés, contracts or other documents (up to {{ maxMb }} MB each). }</p>
      }
    }
  `,
  styles: `
    :host { display: block; margin: 16px 0; }
    .head { display: flex; align-items: center; }
    h3 { margin: 0; font-size: 1rem; }
    .file { display: flex; align-items: center; gap: 8px; padding: 6px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .type { color: var(--mat-sys-primary); }
    .meta { flex: 1; min-width: 0; }
    .name { all: unset; cursor: pointer; color: var(--mat-sys-primary); overflow-wrap: anywhere; }
    .name:hover, .name:focus-visible { text-decoration: underline; }
    .small { font-size: 0.85em; margin: 2px 0 0; }
  `,
})
export class AttachmentsPanel {
  readonly objectKey = input.required<string>();
  readonly recordId = input.required<string>();
  readonly ownerId = input.required<string>();
  /** Emits after an upload or delete, so the host can refresh its history. */
  readonly changed = output<void>();

  private readonly api = inject(CrmApi);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  readonly accept = ATTACHMENT_EXTENSIONS.join(',');
  readonly maxMb = MAX_ATTACHMENT_MB;
  readonly busy = signal(false);
  readonly icon = fileIcon;
  readonly size = formatBytes;

  readonly canEdit = computed(() => this.auth.canModify(this.objectKey(), this.ownerId()));
  readonly files = httpResource<AttachmentItem[]>(() => ({
    url: '/api/attachments',
    params: { objectKey: this.objectKey(), recordId: this.recordId() },
  }));
  readonly items = computed(() => (this.files.hasValue() ? this.files.value() : []));

  upload(picker: HTMLInputElement): void {
    const selected = Array.from(picker.files ?? []);
    picker.value = '';
    const problems = selected.map(checkAttachment).filter((p): p is string => !!p);
    if (problems.length) this.notify.warn(problems.join(' '));
    const valid = selected.filter((f) => !checkAttachment(f));
    if (!valid.length) return;

    this.busy.set(true);
    let pending = valid.length;
    let uploaded = 0;
    const finish = () => {
      if (--pending > 0) return;
      this.busy.set(false);
      this.files.reload();
      this.changed.emit();
      if (uploaded) this.notify.success(uploaded === 1 ? 'File uploaded' : `${uploaded} files uploaded`);
    };
    for (const file of valid) {
      this.api.uploadAttachment(this.objectKey(), this.recordId(), file).subscribe({
        next: () => {
          uploaded++;
          finish();
        },
        error: (err: HttpErrorResponse) => {
          if (err.status === 400) this.notify.warn(`${file.name}: ${errorMessage(err)}`);
          finish();
        },
      });
    }
  }

  download(f: AttachmentItem): void {
    this.api.downloadAttachment(f.id).subscribe((blob) => {
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = f.fileName;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    });
  }

  remove(f: AttachmentItem): void {
    confirm(this.dialog, { title: 'Delete file?', message: `"${f.fileName}" will be permanently deleted.`, confirmText: 'Delete', danger: true })
      .subscribe((ok) => {
        if (!ok) return;
        this.api.deleteAttachment(f.id).subscribe(() => {
          this.files.reload();
          this.changed.emit();
          this.notify.success('File deleted');
        });
      });
  }
}
