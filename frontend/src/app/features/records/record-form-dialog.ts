import { LowerCasePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { CrmApi } from '../../core/api';
import { MetadataService } from '../../core/metadata.service';
import { RecordItem, RecordRequest } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { CustomFieldsEditor, customFieldsGroup, customFieldsValue } from '../../shared/custom-fields';
import { OwnerField } from '../../shared/owner-field';

export interface RecordFormData {
  objectKey: string;
  item?: RecordItem;
}

/** Create/edit form for any tenant-defined object, generated from its field definitions. */
@Component({
  selector: 'app-record-form-dialog',
  imports: [LowerCasePipe, ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, OwnerField, CustomFieldsEditor],
  template: `
    <h2 mat-dialog-title>{{ item ? 'Edit' : 'New' }} {{ object?.label | lowercase }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid" id="record-form" (ngSubmit)="save()">
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>{{ object?.nameLabel ?? 'Name' }}</mat-label>
          <input matInput formControlName="name" maxlength="200" required />
          @if (form.controls.name.hasError('required')) { <mat-error>Required</mat-error> }
        </mat-form-field>
        @if (object?.hasPipeline) {
          <mat-form-field appearance="outline">
            <mat-label>Stage</mat-label>
            <mat-select formControlName="stage">
              @for (s of stages; track s.key) { <mat-option [value]="s.key">{{ s.label }}</mat-option> }
            </mat-select>
          </mat-form-field>
        }
        <app-owner-field [resource]="data.objectKey" [control]="form.controls.ownerId" />
        <app-custom-fields-editor [fields]="fields" [group]="cf" />
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="record-form" [disabled]="saving()">Save</button>
    </mat-dialog-actions>
  `,
})
export class RecordFormDialog {
  readonly data = inject<RecordFormData>(MAT_DIALOG_DATA);
  readonly item = this.data.item;
  private readonly ref = inject(MatDialogRef<RecordFormDialog, RecordItem>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  private readonly meta = inject(MetadataService);
  readonly object = this.meta.object(this.data.objectKey);
  readonly stages = this.meta.stages(this.data.objectKey);
  readonly fields = this.meta.fields(this.data.objectKey);
  readonly cf = customFieldsGroup(this.fields, this.item?.customFields);
  readonly saving = signal(false);

  readonly form = inject(FormBuilder).group({
    name: [this.item?.name ?? '', [Validators.required, Validators.maxLength(200)]],
    stage: [this.item?.stage ?? this.meta.openStages(this.data.objectKey)[0]?.key ?? null],
    ownerId: [this.item?.ownerId ?? null as string | null],
  });

  save(): void {
    if (this.form.invalid || this.cf.invalid) {
      this.form.markAllAsTouched();
      this.cf.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const body: RecordRequest = { name: v.name!, stage: v.stage, ownerId: v.ownerId, customFields: customFieldsValue(this.fields, this.cf) };
    const api = this.api.records(this.data.objectKey);
    this.saving.set(true);
    (this.item ? api.update(this.item.id, body) : api.create(body)).subscribe({
      next: (saved) => {
        this.notify.success(`${this.object?.label ?? 'Record'} saved`);
        this.ref.close(saved);
      },
      error: (err) => {
        this.saving.set(false);
        this.notify.formError(err);
      },
    });
  }
}
