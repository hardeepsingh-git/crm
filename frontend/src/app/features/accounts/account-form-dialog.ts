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
import { AccountDetail, AccountRequest } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { CustomFieldsEditor, customFieldsGroup, customFieldsValue } from '../../shared/custom-fields';
import { OwnerField } from '../../shared/owner-field';
import { withCurrent } from '../../shared/list';

@Component({
  selector: 'app-account-form-dialog',
  imports: [LowerCasePipe, ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, OwnerField, CustomFieldsEditor],
  template: `
    <h2 mat-dialog-title>{{ item ? 'Edit' : 'New' }} {{ meta.label('account') | lowercase }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid" id="account-form" (ngSubmit)="save()">
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>{{ meta.object('account')?.nameLabel ?? 'Name' }}</mat-label>
          <input matInput formControlName="name" maxlength="200" required />
          @if (form.controls.name.hasError('required')) { <mat-error>Name is required</mat-error> }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Industry</mat-label>
          @if (industries.length) {
            <mat-select formControlName="industry">
              <mat-option [value]="null">None</mat-option>
              @for (i of industries; track i) { <mat-option [value]="i">{{ i }}</mat-option> }
            </mat-select>
          } @else {
            <input matInput formControlName="industry" maxlength="100" />
          }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Website</mat-label><input matInput formControlName="website" placeholder="https://" maxlength="300" />
          @if (form.controls.website.hasError('pattern')) { <mat-error>Must start with http:// or https://</mat-error> }
        </mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Phone</mat-label><input matInput formControlName="phone" maxlength="50" /></mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Email</mat-label><input matInput type="email" formControlName="email" maxlength="256" />
          @if (form.controls.email.hasError('email')) { <mat-error>Invalid email</mat-error> }
        </mat-form-field>
        <mat-form-field appearance="outline" class="span-2"><mat-label>Street</mat-label><input matInput formControlName="street" maxlength="200" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>City</mat-label><input matInput formControlName="city" maxlength="100" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>State</mat-label><input matInput formControlName="state" maxlength="100" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Postal code</mat-label><input matInput formControlName="postalCode" maxlength="20" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Country</mat-label><input matInput formControlName="country" maxlength="100" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Annual revenue</mat-label><input matInput type="number" min="0" formControlName="annualRevenue" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Employees</mat-label><input matInput type="number" min="0" formControlName="employeeCount" /></mat-form-field>
        <app-owner-field class="span-2" resource="account" [control]="form.controls.ownerId" />
        <app-custom-fields-editor [fields]="fields" [group]="cf" />
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Description</mat-label><textarea matInput rows="3" formControlName="description" maxlength="4000"></textarea>
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="account-form" [disabled]="saving()">Save</button>
    </mat-dialog-actions>
  `,
})
export class AccountFormDialog {
  readonly item = inject<AccountDetail | null>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<AccountFormDialog, AccountDetail>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly meta = inject(MetadataService);
  readonly saving = signal(false);
  readonly fields = this.meta.fields('account');
  readonly cf = customFieldsGroup(this.fields, this.item?.customFields);
  readonly industries = withCurrent(this.meta.picklist('account.industry'), this.item?.industry);

  readonly form = inject(FormBuilder).group({
    name: [this.item?.name ?? '', [Validators.required, Validators.maxLength(200)]],
    industry: [this.item?.industry ?? null],
    website: [this.item?.website ?? null, Validators.pattern(/^https?:\/\/.+/i)],
    phone: [this.item?.phone ?? null],
    email: [this.item?.email ?? null, Validators.email],
    street: [this.item?.street ?? null],
    city: [this.item?.city ?? null],
    state: [this.item?.state ?? null],
    postalCode: [this.item?.postalCode ?? null],
    country: [this.item?.country ?? null],
    annualRevenue: [this.item?.annualRevenue ?? null as number | null, Validators.min(0)],
    employeeCount: [this.item?.employeeCount ?? null as number | null, Validators.min(0)],
    description: [this.item?.description ?? null],
    ownerId: [this.item?.ownerId ?? null as string | null],
  });

  save(): void {
    if (this.form.invalid || this.cf.invalid) {
      this.form.markAllAsTouched();
      this.cf.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const body = { ...v, name: v.name!, customFields: customFieldsValue(this.fields, this.cf) } as AccountRequest;
    this.saving.set(true);
    (this.item ? this.api.accounts.update(this.item.id, body) : this.api.accounts.create(body)).subscribe({
      next: (saved) => {
        this.notify.success('Account saved');
        this.ref.close(saved);
      },
      error: (err) => {
        this.saving.set(false);
        this.notify.formError(err);
      },
    });
  }
}
