import { LowerCasePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { CrmApi } from '../../core/api';
import { MetadataService } from '../../core/metadata.service';
import { ContactDetail, ContactRequest } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { CustomFieldsEditor, customFieldsGroup, customFieldsValue } from '../../shared/custom-fields';
import { EntityPicker } from '../../shared/entity-picker';
import { OwnerField } from '../../shared/owner-field';

export interface ContactFormData {
  item?: ContactDetail;
  accountId?: string;
}

@Component({
  selector: 'app-contact-form-dialog',
  imports: [LowerCasePipe, ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, EntityPicker, OwnerField, CustomFieldsEditor],
  template: `
    <h2 mat-dialog-title>{{ item ? 'Edit' : 'New' }} {{ meta.label('contact') | lowercase }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid" id="contact-form" (ngSubmit)="save()">
        <mat-form-field appearance="outline">
          <mat-label>First name</mat-label><input matInput formControlName="firstName" maxlength="100" required />
          @if (form.controls.firstName.hasError('required')) { <mat-error>Required</mat-error> }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Last name</mat-label><input matInput formControlName="lastName" maxlength="100" required />
          @if (form.controls.lastName.hasError('required')) { <mat-error>Required</mat-error> }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Email</mat-label><input matInput type="email" formControlName="email" maxlength="256" />
          @if (form.controls.email.hasError('email')) { <mat-error>Invalid email</mat-error> }
        </mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Phone</mat-label><input matInput formControlName="phone" maxlength="50" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Mobile</mat-label><input matInput formControlName="mobile" maxlength="50" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Job title</mat-label><input matInput formControlName="jobTitle" maxlength="100" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Department</mat-label><input matInput formControlName="department" maxlength="100" /></mat-form-field>
        <app-entity-picker [label]="meta.label('account')" kind="account" formControlName="accountId" />
        <app-owner-field class="span-2" resource="contact" [control]="form.controls.ownerId" />
        <app-custom-fields-editor [fields]="fields" [group]="cf" />
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Notes</mat-label><textarea matInput rows="3" formControlName="description" maxlength="4000"></textarea>
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="contact-form" [disabled]="saving()">Save</button>
    </mat-dialog-actions>
  `,
})
export class ContactFormDialog {
  private readonly data = inject<ContactFormData | null>(MAT_DIALOG_DATA) ?? {};
  readonly item = this.data.item;
  private readonly ref = inject(MatDialogRef<ContactFormDialog, ContactDetail>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly meta = inject(MetadataService);
  readonly saving = signal(false);
  readonly fields = this.meta.fields('contact');
  readonly cf = customFieldsGroup(this.fields, this.item?.customFields);

  readonly form = inject(FormBuilder).group({
    firstName: [this.item?.firstName ?? '', [Validators.required, Validators.maxLength(100)]],
    lastName: [this.item?.lastName ?? '', [Validators.required, Validators.maxLength(100)]],
    email: [this.item?.email ?? null, Validators.email],
    phone: [this.item?.phone ?? null],
    mobile: [this.item?.mobile ?? null],
    jobTitle: [this.item?.jobTitle ?? null],
    department: [this.item?.department ?? null],
    description: [this.item?.description ?? null],
    accountId: [this.item?.accountId ?? this.data.accountId ?? null],
    ownerId: [this.item?.ownerId ?? null as string | null],
  });

  save(): void {
    if (this.form.invalid || this.cf.invalid) {
      this.form.markAllAsTouched();
      this.cf.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const body: ContactRequest = { ...v, firstName: v.firstName!, lastName: v.lastName!, customFields: customFieldsValue(this.fields, this.cf) };
    this.saving.set(true);
    (this.item ? this.api.contacts.update(this.item.id, body) : this.api.contacts.create(body)).subscribe({
      next: (saved) => {
        this.notify.success('Contact saved');
        this.ref.close(saved);
      },
      error: (err) => {
        this.saving.set(false);
        this.notify.formError(err);
      },
    });
  }
}
