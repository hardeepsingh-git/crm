import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormBuilder, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { CrmApi, FieldRequest } from '../../core/api';
import { MetadataService } from '../../core/metadata.service';
import { FIELD_TYPES, FieldDef, ObjectDef, StageCategory, StageDef, UNIQUE_TYPES } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { confirm } from '../../shared/confirm-dialog';
import { HumanizePipe } from '../../shared/list';
import { ObjectFormDialog, toKey } from './object-list';
import { SettingsBack } from './settings-home';

interface FieldDialogData {
  objectKey: string;
  item: FieldDef | null;
  nextSortOrder: number;
}

@Component({
  selector: 'app-field-form-dialog',
  imports: [NgTemplateOutlet, ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule, HumanizePipe],
  template: `
    <h2 mat-dialog-title>{{ item ? 'Edit field' : 'New field' }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid" id="field-form" (ngSubmit)="save()">
        <mat-form-field appearance="outline">
          <mat-label>Label</mat-label>
          <input matInput formControlName="label" maxlength="100" required (input)="suggest()" />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>API key</mat-label>
          <input matInput formControlName="key" maxlength="40" required />
          <mat-hint>{{ item ? 'Cannot be changed' : 'Used in filters (?cf.key=) and automation' }}</mat-hint>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Type</mat-label>
          <mat-select formControlName="type">
            @for (t of types; track t) { <mat-option [value]="t">{{ t | humanize }}</mat-option> }
          </mat-select>
          @if (item) { <mat-hint>A field's type cannot be changed</mat-hint> }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Order</mat-label>
          <input matInput type="number" formControlName="sortOrder" />
        </mat-form-field>
        @switch (type()) {
          @case ('Picklist') { <ng-container *ngTemplateOutlet="options" /> }
          @case ('MultiPicklist') { <ng-container *ngTemplateOutlet="options" /> }
          @case ('Lookup') {
            <mat-form-field appearance="outline" class="span-2">
              <mat-label>Looks up</mat-label>
              <mat-select formControlName="lookupObjectKey" required>
                @for (o of meta.objects(); track o.key) { <mat-option [value]="o.key">{{ o.label }}</mat-option> }
              </mat-select>
            </mat-form-field>
          }
          @case ('Number') { <ng-container *ngTemplateOutlet="range" /> }
          @case ('Currency') { <ng-container *ngTemplateOutlet="range" /> }
          @case ('Text') { <ng-container *ngTemplateOutlet="length" /> }
          @case ('TextArea') { <ng-container *ngTemplateOutlet="length" /> }
        }
        <ng-template #options>
          <mat-form-field appearance="outline" class="span-2">
            <mat-label>Options (one per line)</mat-label>
            <textarea matInput rows="5" formControlName="options" required></textarea>
          </mat-form-field>
        </ng-template>
        <ng-template #range>
          <mat-form-field appearance="outline"><mat-label>Minimum</mat-label><input matInput type="number" formControlName="min" /></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum</mat-label><input matInput type="number" formControlName="max" /></mat-form-field>
        </ng-template>
        <ng-template #length>
          <mat-form-field appearance="outline"><mat-label>Max length</mat-label><input matInput type="number" min="1" max="4000" formControlName="maxLength" /></mat-form-field>
        </ng-template>
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Help text</mat-label>
          <input matInput formControlName="helpText" maxlength="500" />
        </mat-form-field>
        <mat-checkbox formControlName="isRequired">Required</mat-checkbox>
        <mat-checkbox formControlName="showInList">Show as a list column (and filter, for picklists/yes-no/lookups)</mat-checkbox>
        @if (canBeUnique()) {
          <mat-checkbox formControlName="isUnique" class="span-2">Unique: no two records can share a value (e.g. a candidate's email)</mat-checkbox>
        }
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="field-form" [disabled]="saving()">Save</button>
    </mat-dialog-actions>
  `,
})
export class FieldFormDialog {
  readonly data = inject<FieldDialogData>(MAT_DIALOG_DATA);
  readonly item = this.data.item;
  private readonly ref = inject(MatDialogRef<FieldFormDialog, FieldDef>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly meta = inject(MetadataService);
  readonly types = FIELD_TYPES;
  readonly saving = signal(false);

  readonly form = inject(FormBuilder).nonNullable.group({
    key: [{ value: this.item?.key ?? '', disabled: !!this.item }, [Validators.required, Validators.pattern(/^[a-z][a-z0-9_]{1,39}$/)]],
    label: [this.item?.label ?? '', Validators.required],
    type: [{ value: this.item?.type ?? 'Text', disabled: !!this.item }],
    isRequired: [this.item?.isRequired ?? false],
    options: [(this.item?.options ?? []).join('\n')],
    lookupObjectKey: [this.item?.lookupObjectKey ?? ''],
    maxLength: [this.item?.maxLength ?? (null as number | null)],
    min: [this.item?.min ?? (null as number | null)],
    max: [this.item?.max ?? (null as number | null)],
    helpText: [this.item?.helpText ?? ''],
    showInList: [this.item?.showInList ?? false],
    isUnique: [this.item?.isUnique ?? false],
    sortOrder: [this.item?.sortOrder ?? this.data.nextSortOrder],
  });
  readonly type = signal(this.form.controls.type.value);
  readonly canBeUnique = computed(() => UNIQUE_TYPES.includes(this.type()));

  constructor() {
    this.form.controls.type.valueChanges.subscribe((t) => this.type.set(t));
  }

  suggest(): void {
    if (!this.item && !this.form.controls.key.dirty) this.form.controls.key.setValue(toKey(this.form.controls.label.value));
  }

  save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const num = (x: number | null) => (x === null || (x as unknown) === '' ? null : Number(x));
    const body: FieldRequest = {
      key: v.key,
      label: v.label.trim(),
      type: v.type,
      isRequired: v.isRequired,
      options: v.options.split('\n').map((o) => o.trim()).filter((o) => o),
      lookupObjectKey: v.type === 'Lookup' ? v.lookupObjectKey || null : null,
      maxLength: num(v.maxLength),
      min: num(v.min),
      max: num(v.max),
      helpText: v.helpText.trim() || null,
      showInList: v.showInList,
      sortOrder: Number(v.sortOrder),
      isUnique: UNIQUE_TYPES.includes(v.type) && v.isUnique,
    };
    this.saving.set(true);
    this.api.saveField(this.data.objectKey, this.item?.key ?? null, body).subscribe({
      next: (saved) => {
        this.notify.success('Field saved');
        this.ref.close(saved);
      },
      error: (err) => {
        this.saving.set(false);
        this.notify.formError(err);
      },
    });
  }
}

const CATEGORIES: StageCategory[] = ['Open', 'Won', 'Lost'];

@Component({
  selector: 'app-object-editor',
  imports: [FormsModule, RouterLink, MatButtonModule, MatCardModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatTableModule, MatTooltipModule, HumanizePipe, SettingsBack],
  template: `
    @if (object(); as o) {
      <div class="page-header">
        <div>
          <app-settings-back />
          <h1><mat-icon class="title-icon">{{ o.icon }}</mat-icon> {{ o.pluralLabel }}</h1>
          <div class="muted">
            {{ o.isStandard ? 'Standard' : 'Custom' }} object &middot; key <code>{{ o.key }}</code>
            &middot; {{ o.isActive ? 'Active' : 'Inactive' }}
            @if (o.description) { &middot; {{ o.description }} }
          </div>
        </div>
        <span class="spacer"></span>
        @if (o.isActive) { <a mat-stroked-button [routerLink]="meta.route(o.key)"><mat-icon>open_in_new</mat-icon> Open</a> }
        <button mat-stroked-button (click)="editObject(o)"><mat-icon>edit</mat-icon> Edit details</button>
        @if (!o.isStandard) {
          <button mat-stroked-button class="danger" (click)="deleteObject(o)"><mat-icon>delete</mat-icon> Delete</button>
        }
      </div>

      <mat-card appearance="outlined" class="section">
        <mat-card-header>
          <mat-card-title>Custom fields</mat-card-title>
          <span class="spacer"></span>
          <button mat-flat-button (click)="editField(o, null)"><mat-icon>add</mat-icon> Add field</button>
        </mat-card-header>
        <mat-card-content>
          @if (o.isStandard) { <p class="muted">Built-in fields (name, owner, etc.) are always present; add your own below.</p> }
          <table mat-table [dataSource]="o.fields">
            <ng-container matColumnDef="label"><th mat-header-cell *matHeaderCellDef>Label</th>
              <td mat-cell *matCellDef="let f">{{ f.label }} @if (f.isRequired) { <span class="req" matTooltip="Required">*</span> } @if (f.isUnique) { <mat-icon inline matTooltip="Unique">fingerprint</mat-icon> }</td></ng-container>
            <ng-container matColumnDef="key"><th mat-header-cell *matHeaderCellDef>Key</th><td mat-cell *matCellDef="let f"><code>{{ f.key }}</code></td></ng-container>
            <ng-container matColumnDef="type"><th mat-header-cell *matHeaderCellDef>Type</th>
              <td mat-cell *matCellDef="let f">{{ f.type | humanize }}
                @if (f.lookupObjectKey) { <span class="muted">&rarr; {{ meta.label(f.lookupObjectKey) }}</span> }
                @if (f.options.length) { <span class="muted">({{ f.options.length }} options)</span> }</td></ng-container>
            <ng-container matColumnDef="list"><th mat-header-cell *matHeaderCellDef>In list</th>
              <td mat-cell *matCellDef="let f">@if (f.showInList) { <mat-icon inline>check</mat-icon> }</td></ng-container>
            <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th>
              <td mat-cell *matCellDef="let f" class="actions">
                <button mat-icon-button (click)="editField(o, f)" aria-label="Edit field"><mat-icon>edit</mat-icon></button>
                <button mat-icon-button (click)="deleteField(o, f)" aria-label="Delete field"><mat-icon>delete</mat-icon></button>
              </td></ng-container>
            <tr mat-header-row *matHeaderRowDef="fieldColumns"></tr>
            <tr mat-row *matRowDef="let row; columns: fieldColumns"></tr>
            <tr class="mat-mdc-row" *matNoDataRow><td class="mat-mdc-cell empty" colspan="5">No custom fields yet.</td></tr>
          </table>
        </mat-card-content>
      </mat-card>

      @if (o.hasPipeline) {
        <mat-card appearance="outlined" class="section">
          <mat-card-header>
            <mat-card-title>Pipeline stages</mat-card-title>
            <span class="spacer"></span>
            <button mat-stroked-button (click)="addStage()"><mat-icon>add</mat-icon> Add stage</button>
            <button mat-flat-button (click)="saveStages(o)" [disabled]="savingStages()">Save stages</button>
          </mat-card-header>
          <mat-card-content>
            <p class="muted">Order matters: open stages form the path shown on records. Won/Lost stages close the record.
              Stages that still contain records can't be removed.</p>
            <table class="stages">
              <thead><tr><th></th><th>Label</th><th>Key</th><th>Category</th><th>Probability %</th><th>Colour</th><th></th></tr></thead>
              <tbody>
                @for (s of stages(); track $index; let i = $index; let last = $last) {
                  <tr>
                    <td class="order">
                      <button mat-icon-button [disabled]="i === 0" (click)="move(i, -1)" aria-label="Move up"><mat-icon>arrow_upward</mat-icon></button>
                      <button mat-icon-button [disabled]="last" (click)="move(i, 1)" aria-label="Move down"><mat-icon>arrow_downward</mat-icon></button>
                    </td>
                    <td><input class="cell" [(ngModel)]="s.label" maxlength="100" (input)="suggestStageKey(s)" aria-label="Stage label" /></td>
                    <td><input class="cell key" [(ngModel)]="s.key" maxlength="64" aria-label="Stage key" /></td>
                    <td>
                      <select class="cell" [(ngModel)]="s.category" aria-label="Category">
                        @for (c of categories; track c) { <option [value]="c">{{ c }}</option> }
                      </select>
                    </td>
                    <td><input class="cell num" type="number" min="0" max="100" [(ngModel)]="s.probability" aria-label="Probability" /></td>
                    <td><input type="color" [(ngModel)]="s.color" aria-label="Colour" /></td>
                    <td><button mat-icon-button (click)="removeStage(i)" aria-label="Remove stage"><mat-icon>close</mat-icon></button></td>
                  </tr>
                }
              </tbody>
            </table>
          </mat-card-content>
        </mat-card>
      }
    } @else {
      <app-settings-back />
      <p class="muted">Object not found.</p>
    }
  `,
  styles: `
    .title-icon { vertical-align: middle; }
    .section { margin-bottom: 16px; }
    mat-card-header { display: flex; align-items: center; gap: 8px; padding-bottom: 8px; }
    td.actions { text-align: right; white-space: nowrap; }
    .req { color: var(--mat-sys-error); }
    .stages { width: 100%; border-collapse: collapse; }
    .stages th { text-align: left; font-weight: 500; color: var(--mat-sys-on-surface-variant); padding: 4px; }
    .stages td { padding: 4px; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .cell { font: inherit; padding: 6px 8px; border-radius: 6px; border: 1px solid var(--mat-sys-outline-variant); background: var(--mat-sys-surface); color: inherit; width: 100%; box-sizing: border-box; }
    .cell.num { width: 80px; }
    .cell.key { font-family: monospace; }
    .order { white-space: nowrap; width: 96px; }
  `,
})
export class ObjectEditor {
  readonly key = input.required<string>();
  readonly meta = inject(MetadataService);
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  readonly fieldColumns = ['label', 'key', 'type', 'list', 'actions'];
  readonly categories = CATEGORIES;
  readonly savingStages = signal(false);

  readonly object = computed(() => this.meta.object(this.key()));
  /** Editable copy of the stages; saved as a whole. */
  readonly stages = signal<StageDef[]>([]);

  constructor() {
    effect(() => this.stages.set((this.object()?.stages ?? []).map((s) => ({ ...s }))));
  }

  editObject(o: ObjectDef): void {
    this.dialog.open(ObjectFormDialog, { data: o, width: '720px' }).afterClosed().subscribe((saved) => saved && this.meta.load());
  }

  deleteObject(o: ObjectDef): void {
    confirm(this.dialog, {
      title: `Delete ${o.label}?`,
      message: `The object, its fields, stages and automation rules will be deleted. Objects that still contain records cannot be deleted.`,
      confirmText: 'Delete',
      danger: true,
    }).subscribe((ok) => {
      if (!ok) return;
      this.api.deleteObject(o.key).subscribe(async () => {
        this.notify.success(`${o.label} deleted`);
        await this.meta.load();
        void this.router.navigateByUrl('/settings/objects');
      });
    });
  }

  editField(o: ObjectDef, f: FieldDef | null): void {
    const nextSortOrder = Math.max(0, ...o.fields.map((x) => x.sortOrder)) + 10;
    this.dialog.open(FieldFormDialog, { data: { objectKey: o.key, item: f, nextSortOrder }, width: '640px' }).afterClosed()
      .subscribe((saved) => saved && this.meta.load());
  }

  deleteField(o: ObjectDef, f: FieldDef): void {
    confirm(this.dialog, {
      title: `Delete "${f.label}"?`,
      message: 'The field definition is removed. Values already stored on records are kept but hidden.',
      confirmText: 'Delete',
      danger: true,
    }).subscribe((ok) => {
      if (!ok) return;
      this.api.deleteField(o.key, f.key).subscribe(() => {
        this.notify.success('Field deleted');
        void this.meta.load();
      });
    });
  }

  addStage(): void {
    this.stages.update((list) => [...list, { key: '', label: '', probability: 50, category: 'Open', color: '#607d8b' }]);
  }

  removeStage(index: number): void {
    this.stages.update((list) => list.filter((_, i) => i !== index));
  }

  move(index: number, delta: number): void {
    this.stages.update((list) => {
      const next = [...list];
      [next[index], next[index + delta]] = [next[index + delta], next[index]];
      return next;
    });
  }

  suggestStageKey(s: StageDef): void {
    const existing = this.object()?.stages.some((x) => x.key === s.key && s.key);
    if (!existing) s.key = s.label.replace(/[^A-Za-z0-9]+/g, '').slice(0, 64);
  }

  saveStages(o: ObjectDef): void {
    const stages = this.stages().map((s) => ({ ...s, probability: Number(s.probability) }));
    this.savingStages.set(true);
    this.api.saveStages(o.key, stages).subscribe({
      next: () => {
        this.savingStages.set(false);
        this.notify.success('Stages saved');
        void this.meta.load();
      },
      error: (err) => {
        this.savingStages.set(false);
        this.notify.formError(err);
      },
    });
  }
}
