import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { CrmApi } from '../../core/api';
import { MetadataService } from '../../core/metadata.service';
import { ImportAnalysis, ImportResult } from '../../core/models';
import { errorMessage } from '../../core/notify.service';

export interface ImportDialogData {
  objectKey: string;
}

type Step = 'pick' | 'map' | 'checked' | 'done';

/** Quotes a CSV cell when needed (RFC 4180). */
export function csvCell(value: string): string {
  return /[",\r\n]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
}

@Component({
  selector: 'app-import-dialog',
  imports: [MatDialogModule, MatButtonModule, MatCheckboxModule, MatIconModule, MatProgressBarModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Import {{ object()?.pluralLabel }}</h2>
    @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
    <mat-dialog-content>
      @switch (step()) {
        @case ('pick') {
          <p>Upload a CSV file with a header row. You'll match its columns to fields and check every row before anything is saved.</p>
          <ul class="muted">
            <li>Link to other records by name, e.g. a job opening's title.</li>
            <li>Dates as yyyy-mm-dd or dd/mm/yyyy; yes/no as Yes or No; multiple choices separated by ;.</li>
            <li>Nothing is imported unless every row is valid. Up to 5,000 rows per file.</li>
          </ul>
          <input #picker type="file" hidden accept=".csv,text/csv" (change)="choose(picker)" />
          <div class="actions-row">
            <button mat-flat-button (click)="picker.click()" [disabled]="busy()"><mat-icon>upload_file</mat-icon> Choose CSV file</button>
            <button mat-button (click)="downloadTemplate()"><mat-icon>download</mat-icon> Download a template</button>
          </div>
        }
        @case ('map') {
          @let a = analysis()!;
          <p><strong>{{ fileName() }}</strong>: {{ a.rowCount }} {{ a.rowCount === 1 ? 'row' : 'rows' }}. Choose the field each column goes into.</p>
          <table class="map">
            <thead><tr><th>Column in file</th><th>Example</th><th>Import into</th></tr></thead>
            <tbody>
              @for (h of a.headers; track h; let i = $index) {
                <tr>
                  <td>{{ h }}</td>
                  <td class="muted sample">{{ example(i) }}</td>
                  <td>
                    <mat-select [value]="mapping()[h] ?? null" (selectionChange)="setTarget(h, $event.value)" [attr.aria-label]="'Field for ' + h">
                      <mat-option [value]="null">Don't import</mat-option>
                      @for (t of a.targets; track t.key) {
                        <mat-option [value]="t.key" [disabled]="takenBy(t.key, h)">{{ t.label }}{{ t.required ? ' *' : '' }}</mat-option>
                      }
                    </mat-select>
                  </td>
                </tr>
              }
            </tbody>
          </table>
          @if (missing().length) {
            <p class="error">Required: {{ missing().join(', ') }}.</p>
          }
          <mat-checkbox [checked]="runAutomation()" (change)="runAutomation.set($event.checked)">
            Run automation rules for imported records
          </mat-checkbox>
          <p class="muted small">Off by default, so importing doesn't create a task or notification for every row.</p>
        }
        @case ('checked') {
          @let r = result()!;
          @if (r.errorCount) {
            <p class="error"><mat-icon inline>error</mat-icon> {{ r.errorCount }} of {{ r.total }} rows have problems. Nothing has been imported.</p>
            <p class="muted">Fix these rows in your file and choose it again, or go back and change the column mapping.</p>
            <ul class="errors">
              @for (e of r.errors; track e.row) {
                <li><strong>Row {{ e.row }}:</strong> {{ e.messages.join(' ') }}</li>
              }
            </ul>
            @if (r.errorCount > r.errors.length) { <p class="muted">…and {{ r.errorCount - r.errors.length }} more rows.</p> }
          } @else {
            <p><mat-icon inline class="ok">check_circle</mat-icon> All {{ r.total }} rows are valid and ready to import.</p>
          }
        }
        @case ('done') {
          <p><mat-icon inline class="ok">check_circle</mat-icon> Imported {{ result()!.created }} {{ object()?.pluralLabel?.toLowerCase() }}.</p>
        }
      }
      @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @switch (step()) {
        @case ('pick') { <button mat-button mat-dialog-close>Cancel</button> }
        @case ('map') {
          <button mat-button mat-dialog-close>Cancel</button>
          <button mat-button (click)="reset()">Choose another file</button>
          <button mat-flat-button (click)="check()" [disabled]="busy() || missing().length > 0">Check file</button>
        }
        @case ('checked') {
          <button mat-button mat-dialog-close>Cancel</button>
          <button mat-button (click)="step.set('map')">Back to mapping</button>
          @if (result()!.errorCount) {
            <button mat-stroked-button (click)="reset()">Choose fixed file</button>
          } @else {
            <button mat-flat-button (click)="import()" [disabled]="busy()">Import {{ result()!.total }} rows</button>
          }
        }
        @case ('done') { <button mat-flat-button [mat-dialog-close]="result()!.created">Done</button> }
      }
    </mat-dialog-actions>
  `,
  styles: `
    .actions-row { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 8px; }
    .map { width: 100%; border-collapse: collapse; margin-bottom: 12px; }
    .map th { text-align: left; font-weight: 500; padding: 4px 8px; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .map td { padding: 6px 8px; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .sample { max-width: 180px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .errors { max-height: 260px; overflow: auto; padding-left: 20px; }
    .error { color: var(--mat-sys-error); }
    .ok { color: #2e7d32; }
    .small { font-size: 0.85em; margin-top: 0; }
  `,
})
export class ImportDialog {
  private readonly data = inject<ImportDialogData>(MAT_DIALOG_DATA);
  private readonly api = inject(CrmApi);
  private readonly meta = inject(MetadataService);
  readonly ref = inject(MatDialogRef<ImportDialog, number>);

  readonly object = computed(() => this.meta.object(this.data.objectKey));
  readonly step = signal<Step>('pick');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly fileName = signal('');
  readonly analysis = signal<ImportAnalysis | null>(null);
  readonly mapping = signal<Record<string, string | null>>({});
  readonly runAutomation = signal(false);
  readonly result = signal<ImportResult | null>(null);
  private csv = '';

  readonly missing = computed(() => {
    const a = this.analysis();
    if (!a) return [];
    const mapped = new Set(Object.values(this.mapping()).filter((v): v is string => !!v));
    return a.targets.filter((t) => t.required && !mapped.has(t.key)).map((t) => t.label);
  });

  async choose(picker: HTMLInputElement): Promise<void> {
    const file = picker.files?.[0];
    picker.value = '';
    if (!file) return;
    if (file.size > 5 * 1024 * 1024) {
      this.error.set('The file is larger than 5 MB. Split it into smaller files.');
      return;
    }
    this.error.set(null);
    this.busy.set(true);
    this.fileName.set(file.name);
    this.csv = await file.text();
    this.api.analyzeImport(this.data.objectKey, this.csv).subscribe({
      next: (a) => {
        this.busy.set(false);
        this.analysis.set(a);
        this.mapping.set({ ...a.suggestedMapping });
        this.step.set('map');
      },
      error: (err: HttpErrorResponse) => this.fail(err),
    });
  }

  example(column: number): string {
    return this.analysis()?.sample.map((row) => row[column]?.trim()).find((v) => !!v) ?? '';
  }

  setTarget(header: string, target: string | null): void {
    this.mapping.update((m) => ({ ...m, [header]: target }));
  }

  takenBy(target: string, header: string): boolean {
    return Object.entries(this.mapping()).some(([h, t]) => t === target && h !== header);
  }

  check(): void {
    this.run(true);
  }

  import(): void {
    this.run(false);
  }

  private run(dryRun: boolean): void {
    this.error.set(null);
    this.busy.set(true);
    this.api.runImport(this.data.objectKey, { csv: this.csv, mapping: this.mapping(), dryRun, runAutomation: this.runAutomation() }).subscribe({
      next: (r) => {
        this.busy.set(false);
        this.result.set(r);
        this.step.set(dryRun || r.errorCount ? 'checked' : 'done');
      },
      error: (err: HttpErrorResponse) => this.fail(err),
    });
  }

  reset(): void {
    this.csv = '';
    this.analysis.set(null);
    this.result.set(null);
    this.error.set(null);
    this.step.set('pick');
  }

  downloadTemplate(): void {
    const obj = this.object();
    if (!obj) return;
    const headers = [obj.nameLabel, ...(obj.hasPipeline ? ['Stage'] : []), 'Owner', ...this.meta.fields(obj.key).map((f) => f.label)];
    const blob = new Blob([headers.map(csvCell).join(',') + '\r\n'], { type: 'text/csv' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${obj.pluralLabel.toLowerCase().replace(/\s+/g, '-')}-template.csv`;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  private fail(err: HttpErrorResponse): void {
    this.busy.set(false);
    this.error.set(errorMessage(err));
  }
}
