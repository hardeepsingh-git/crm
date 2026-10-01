import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { CrmApi } from '../../core/api';
import { MetadataService } from '../../core/metadata.service';
import { NotifyService } from '../../core/notify.service';
import { SettingsBack } from './settings-home';

interface PicklistDef {
  key: string;
  title: string;
  hint: string;
  /** Shown only when this object is active. */
  requires?: string;
}

const PICKLISTS: PicklistDef[] = [
  { key: 'account.industry', title: 'Account industries', hint: 'Options for the Industry field on accounts. Leave empty to allow free text.' },
  { key: 'lead.source', title: 'Lead sources', hint: 'Where leads come from. Used on leads and in the dashboard.' },
  { key: 'interview.criteria', title: 'Interview criteria', hint: 'What interviewers rate from 1 to 5 on their scorecards.', requires: 'application' },
];

@Component({
  selector: 'app-picklists',
  imports: [FormsModule, MatButtonModule, MatCardModule, MatFormFieldModule, MatInputModule, SettingsBack],
  template: `
    <div class="page-header"><div><app-settings-back /><h1>Picklists</h1></div></div>
    <p class="muted">Custom picklist fields keep their options on the field itself (Settings &rsaquo; Objects &amp; fields).</p>
    <div class="grid">
      @for (p of picklists; track p.key) {
        <mat-card appearance="outlined">
          <mat-card-header><mat-card-title>{{ p.title }}</mat-card-title></mat-card-header>
          <mat-card-content>
            <p class="muted">{{ p.hint }}</p>
            <mat-form-field appearance="outline" class="full">
              <mat-label>Options (one per line)</mat-label>
              <textarea matInput rows="10" [(ngModel)]="values[p.key]"></textarea>
            </mat-form-field>
            <button mat-flat-button (click)="save(p)" [disabled]="saving() === p.key">Save</button>
          </mat-card-content>
        </mat-card>
      }
    </div>
  `,
  styles: `.grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(340px, 1fr)); gap: 16px; } .full { width: 100%; }`,
})
export class Picklists {
  private readonly meta = inject(MetadataService);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly picklists = PICKLISTS.filter((p) => !p.requires || this.meta.isActive(p.requires));
  readonly saving = signal<string | null>(null);
  readonly values: Record<string, string> = Object.fromEntries(this.picklists.map((p) => [p.key, this.meta.picklist(p.key).join('\n')]));

  save(p: PicklistDef): void {
    const options = this.values[p.key].split('\n').map((o) => o.trim()).filter((o) => o);
    this.saving.set(p.key);
    this.api.savePicklist(p.key, options).subscribe({
      next: () => {
        this.saving.set(null);
        this.notify.success(`${p.title} saved`);
        void this.meta.load();
      },
      error: (err) => {
        this.saving.set(null);
        this.notify.formError(err);
      },
    });
  }
}
