import { HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Component, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { CrmApi } from '../core/api';
import { NotifyService } from '../core/notify.service';

/** Downloads the list as CSV, with the same filters as on screen. */
@Component({
  selector: 'app-export-button',
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  template: `
    <button mat-stroked-button (click)="download()" [disabled]="busy()" matTooltip="Download the filtered list as a CSV file">
      <mat-icon>download</mat-icon> Export
    </button>
  `,
})
export class ExportButton {
  readonly objectKey = input.required<string>();
  readonly params = input.required<HttpParams>();
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly busy = signal(false);

  download(): void {
    this.busy.set(true);
    this.api.exportCsv(this.objectKey(), this.params()).subscribe({
      next: () => this.busy.set(false),
      error: (err: unknown) => {
        this.busy.set(false);
        // Downloads ask for a blob, so a 400 (too many rows) arrives as a blob too; its message says how to narrow the filters.
        if (err instanceof HttpErrorResponse && err.status === 400 && err.error instanceof Blob) {
          void err.error.text().then((text) => {
            try {
              const problem = JSON.parse(text) as { errors?: Record<string, string[]>; detail?: string };
              this.notify.warn(Object.values(problem.errors ?? {}).flat().join(' ') || problem.detail || 'The export failed.');
            } catch {
              this.notify.warn('The export failed.');
            }
          });
        }
      },
    });
  }
}
