import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatTableModule } from '@angular/material/table';
import { Router, RouterLink } from '@angular/router';
import { CrmApi } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { MetadataService } from '../../core/metadata.service';
import { ObjectDef } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SettingsBack } from './settings-home';

/** "Support Ticket" -> "support_ticket". */
export function toKey(label: string): string {
  return label
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^[^a-z]+/, '')
    .replace(/_+$/, '')
    .slice(0, 40);
}

export const ICON_SUGGESTIONS = [
  'category', 'confirmation_number', 'work', 'assignment_ind', 'inventory_2', 'home_work', 'event', 'school', 'local_shipping',
  'build', 'receipt_long', 'support_agent', 'real_estate_agent', 'volunteer_activism', 'medical_services', 'storefront',
];

@Component({
  selector: 'app-object-form-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ item ? 'Edit ' + item.label : 'New object' }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid" id="object-form" (ngSubmit)="save()">
        <mat-form-field appearance="outline">
          <mat-label>Label (singular)</mat-label>
          <input matInput formControlName="label" maxlength="100" required (input)="suggest()" />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Plural label</mat-label>
          <input matInput formControlName="pluralLabel" maxlength="100" required />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>API key</mat-label>
          <input matInput formControlName="key" maxlength="40" required />
          <mat-hint>{{ item ? 'Cannot be changed' : 'Lowercase letters, digits, underscores' }}</mat-hint>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Name field label</mat-label>
          <input matInput formControlName="nameLabel" maxlength="100" required />
          <mat-hint>e.g. "Subject", "Title", "Name"</mat-hint>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Icon</mat-label>
          <mat-icon matPrefix>{{ form.controls.icon.value }}</mat-icon>
          <input matInput formControlName="icon" maxlength="64" required />
          <mat-hint>A Material Symbols name</mat-hint>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Menu order</mat-label>
          <input matInput type="number" formControlName="sortOrder" />
        </mat-form-field>
        <div class="span-2 icons">
          @for (i of icons; track i) {
            <button type="button" mat-icon-button (click)="form.controls.icon.setValue(i)" [attr.aria-label]="i"><mat-icon>{{ i }}</mat-icon></button>
          }
        </div>
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Description</mat-label>
          <textarea matInput rows="2" formControlName="description" maxlength="1000"></textarea>
        </mat-form-field>
        <mat-checkbox formControlName="hasPipeline">Has a pipeline (stages, board view)</mat-checkbox>
        <mat-checkbox formControlName="isActive">Active (shown in navigation)</mat-checkbox>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="object-form" [disabled]="saving()">Save</button>
    </mat-dialog-actions>
  `,
  styles: `.icons { display: flex; flex-wrap: wrap; margin-top: -8px; }`,
})
export class ObjectFormDialog {
  readonly item = inject<ObjectDef | null>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<ObjectFormDialog, ObjectDef>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly icons = ICON_SUGGESTIONS;
  readonly saving = signal(false);

  readonly form = inject(FormBuilder).nonNullable.group({
    key: [{ value: this.item?.key ?? '', disabled: !!this.item }, [Validators.required, Validators.pattern(/^[a-z][a-z0-9_]{1,39}$/)]],
    label: [this.item?.label ?? '', Validators.required],
    pluralLabel: [this.item?.pluralLabel ?? '', Validators.required],
    icon: [this.item?.icon ?? 'category', Validators.required],
    description: [this.item?.description ?? ''],
    nameLabel: [this.item?.nameLabel ?? 'Name', Validators.required],
    hasPipeline: [{ value: this.item?.hasPipeline ?? false, disabled: !!this.item?.isStandard }],
    isActive: [this.item?.isActive ?? true],
    sortOrder: [this.item?.sortOrder ?? 50],
  });

  suggest(): void {
    if (this.item) return;
    const label = this.form.controls.label.value;
    if (!this.form.controls.key.dirty) this.form.controls.key.setValue(toKey(label));
    if (!this.form.controls.pluralLabel.dirty) this.form.controls.pluralLabel.setValue(label ? `${label}s` : '');
  }

  save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    this.saving.set(true);
    this.api
      .saveObject(this.item?.key ?? null, { ...v, key: this.item ? undefined : v.key, description: v.description.trim() || null, sortOrder: Number(v.sortOrder) })
      .subscribe({
        next: (saved) => {
          this.notify.success('Object saved');
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
  selector: 'app-object-list',
  imports: [RouterLink, MatTableModule, MatButtonModule, MatCardModule, MatIconModule, SettingsBack],
  template: `
    <div class="page-header">
      <div><app-settings-back /><h1>Objects &amp; fields</h1></div>
      <span class="spacer"></span>
      <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> New object</button>
    </div>
    <p class="muted">Rename standard objects, switch off ones you don't use, add custom fields, or create entirely new objects (tickets, projects, properties&hellip;).</p>
    <div class="table-wrap">
      <table mat-table [dataSource]="meta.objects()">
        <ng-container matColumnDef="label">
          <th mat-header-cell *matHeaderCellDef>Object</th>
          <td mat-cell *matCellDef="let o"><mat-icon inline>{{ o.icon }}</mat-icon> <a [routerLink]="o.key">{{ o.pluralLabel }}</a>
            <span class="muted"> ({{ o.key }})</span></td>
        </ng-container>
        <ng-container matColumnDef="type">
          <th mat-header-cell *matHeaderCellDef>Type</th>
          <td mat-cell *matCellDef="let o">{{ o.isStandard ? 'Standard' : 'Custom' }}{{ o.hasPipeline ? ' · pipeline' : '' }}</td>
        </ng-container>
        <ng-container matColumnDef="fields">
          <th mat-header-cell *matHeaderCellDef>Custom fields</th>
          <td mat-cell *matCellDef="let o">{{ o.fields.length }}</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>Status</th>
          <td mat-cell *matCellDef="let o"><span class="chip" [attr.data-v]="o.isActive ? 'Active' : 'Inactive'">{{ o.isActive ? 'Active' : 'Inactive' }}</span></td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="open(row)"></tr>
      </table>
    </div>
  `,
})
export class ObjectList {
  readonly meta = inject(MetadataService);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  readonly columns = ['label', 'type', 'fields', 'status'];

  open(o: ObjectDef): void {
    void this.router.navigate(['/settings/objects', o.key]);
  }

  create(): void {
    this.dialog.open(ObjectFormDialog, { data: null, width: '720px' }).afterClosed().subscribe(async (saved?: ObjectDef) => {
      if (!saved) return;
      await this.meta.load();
      // Administrators see new objects immediately; other roles need the object granted (Settings > Roles).
      this.auth.reloadUser();
      void this.router.navigate(['/settings/objects', saved.key]);
    });
  }
}
