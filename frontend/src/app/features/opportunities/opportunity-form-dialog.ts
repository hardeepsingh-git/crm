import { LowerCasePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { CrmApi } from '../../core/api';
import { MetadataService } from '../../core/metadata.service';
import { OpportunityDetail, OpportunityRequest } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { CustomFieldsEditor, customFieldsGroup, customFieldsValue } from '../../shared/custom-fields';
import { EntityPicker } from '../../shared/entity-picker';
import { todayPlus } from '../../shared/list';
import { OwnerField } from '../../shared/owner-field';

export interface OpportunityFormData {
  item?: OpportunityDetail;
  accountId?: string;
  contactId?: string;
}

@Component({
  selector: 'app-opportunity-form-dialog',
  imports: [LowerCasePipe, ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, EntityPicker, OwnerField, CustomFieldsEditor],
  template: `
    <h2 mat-dialog-title>{{ item ? 'Edit' : 'New' }} {{ meta.label('opportunity') | lowercase }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid" id="opp-form" (ngSubmit)="save()">
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>{{ meta.object('opportunity')?.nameLabel ?? 'Name' }}</mat-label><input matInput formControlName="name" maxlength="200" required />
          @if (form.controls.name.hasError('required')) { <mat-error>Required</mat-error> }
        </mat-form-field>
        <app-entity-picker [label]="meta.label('account')" kind="account" formControlName="accountId" [required]="true" />
        <app-entity-picker label="Primary contact" kind="contact" formControlName="contactId"
          [extraParams]="{ accountId: form.controls.accountId.value }" />
        <mat-form-field appearance="outline">
          <mat-label>Amount</mat-label><input matInput type="number" min="0" formControlName="amount" required />
          @if (form.controls.amount.invalid) { <mat-error>Enter an amount of 0 or more</mat-error> }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Expected close date</mat-label><input matInput type="date" formControlName="expectedCloseDate" required />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Stage</mat-label>
          <mat-select formControlName="stage">
            @for (s of stages; track s.key) { <mat-option [value]="s.key">{{ s.label }} ({{ s.probability }}%)</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Probability %</mat-label>
          <input matInput type="number" min="0" max="100" formControlName="probability" placeholder="Stage default" />
          <mat-hint>Leave blank to use the stage default</mat-hint>
        </mat-form-field>
        @if (isLost()) {
          <mat-form-field appearance="outline" class="span-2">
            <mat-label>Loss reason</mat-label><input matInput formControlName="lossReason" maxlength="500" />
          </mat-form-field>
        }
        <app-owner-field class="span-2" resource="opportunity" [control]="form.controls.ownerId" />
        <app-custom-fields-editor [fields]="fields" [group]="cf" />
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Description</mat-label><textarea matInput rows="3" formControlName="description" maxlength="4000"></textarea>
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="opp-form" [disabled]="saving()">Save</button>
    </mat-dialog-actions>
  `,
})
export class OpportunityFormDialog {
  private readonly data = inject<OpportunityFormData | null>(MAT_DIALOG_DATA) ?? {};
  readonly item = this.data.item;
  private readonly ref = inject(MatDialogRef<OpportunityFormDialog, OpportunityDetail>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly meta = inject(MetadataService);
  readonly stages = this.meta.stages('opportunity');
  readonly saving = signal(false);
  readonly fields = this.meta.fields('opportunity');
  readonly cf = customFieldsGroup(this.fields, this.item?.customFields);

  readonly form = inject(FormBuilder).group({
    name: [this.item?.name ?? '', [Validators.required, Validators.maxLength(200)]],
    accountId: [this.item?.accountId ?? this.data.accountId ?? null, Validators.required],
    contactId: [this.item?.contactId ?? this.data.contactId ?? null],
    amount: [this.item?.amount ?? 0, [Validators.required, Validators.min(0)]],
    expectedCloseDate: [this.item?.expectedCloseDate ?? todayPlus(30), Validators.required],
    stage: [this.item?.stage ?? this.meta.openStages('opportunity')[0]?.key ?? ''],
    probability: [null as number | null, [Validators.min(0), Validators.max(100)]],
    lossReason: [this.item?.lossReason ?? null],
    description: [this.item?.description ?? null],
    ownerId: [this.item?.ownerId ?? null as string | null],
  });
  private readonly stage = toSignal(this.form.controls.stage.valueChanges, { initialValue: this.form.controls.stage.value });

  isLost(): boolean {
    return this.meta.stage('opportunity', this.stage() ?? '')?.category === 'Lost';
  }

  save(): void {
    if (this.form.invalid || this.cf.invalid) {
      this.form.markAllAsTouched();
      this.cf.markAllAsTouched();
      if (this.form.controls.accountId.invalid) this.notify.warn('Select an account.');
      return;
    }
    const v = this.form.getRawValue();
    const body: OpportunityRequest = {
      ...v,
      name: v.name!,
      accountId: v.accountId!,
      amount: v.amount!,
      expectedCloseDate: v.expectedCloseDate!,
      stage: v.stage!,
      customFields: customFieldsValue(this.fields, this.cf),
    };
    this.saving.set(true);
    (this.item ? this.api.opportunities.update(this.item.id, body) : this.api.opportunities.create(body)).subscribe({
      next: (saved) => {
        this.notify.success('Opportunity saved');
        this.ref.close(saved);
      },
      error: (err) => {
        this.saving.set(false);
        this.notify.formError(err);
      },
    });
  }
}
