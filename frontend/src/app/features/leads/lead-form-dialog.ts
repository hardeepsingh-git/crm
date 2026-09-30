import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { CrmApi } from '../../core/api';
import { LEAD_SOURCES, LEAD_STATUSES, LeadDetail, LeadRequest, LeadSource, LeadStatus } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { HumanizePipe } from '../../shared/list';
import { OwnerField } from '../../shared/owner-field';

@Component({
  selector: 'app-lead-form-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, OwnerField, HumanizePipe],
  template: `
    <h2 mat-dialog-title>{{ item ? 'Edit lead' : 'New lead' }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid" id="lead-form" (ngSubmit)="save()">
        <mat-form-field appearance="outline">
          <mat-label>First name</mat-label><input matInput formControlName="firstName" maxlength="100" required />
          @if (form.controls.firstName.hasError('required')) { <mat-error>Required</mat-error> }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Last name</mat-label><input matInput formControlName="lastName" maxlength="100" required />
          @if (form.controls.lastName.hasError('required')) { <mat-error>Required</mat-error> }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Company</mat-label><input matInput formControlName="company" maxlength="200" required />
          @if (form.controls.company.hasError('required')) { <mat-error>Required</mat-error> }
        </mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Job title</mat-label><input matInput formControlName="jobTitle" maxlength="100" /></mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Email</mat-label><input matInput type="email" formControlName="email" maxlength="256" />
          @if (form.controls.email.hasError('email')) { <mat-error>Invalid email</mat-error> }
        </mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Phone</mat-label><input matInput formControlName="phone" maxlength="50" /></mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Source</mat-label>
          <mat-select formControlName="source">
            @for (s of sources; track s) { <mat-option [value]="s">{{ s | humanize }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Status</mat-label>
          <mat-select formControlName="status">
            @for (s of statuses; track s) { <mat-option [value]="s">{{ s }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Estimated value</mat-label><input matInput type="number" min="0" formControlName="estimatedValue" /></mat-form-field>
        <app-owner-field [control]="form.controls.ownerId" />
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Description</mat-label><textarea matInput rows="3" formControlName="description" maxlength="4000"></textarea>
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="lead-form" [disabled]="saving()">Save</button>
    </mat-dialog-actions>
  `,
})
export class LeadFormDialog {
  readonly item = inject<LeadDetail | null>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<LeadFormDialog, LeadDetail>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly sources = LEAD_SOURCES;
  readonly statuses = LEAD_STATUSES.filter((s) => s !== 'Converted');
  readonly saving = signal(false);

  readonly form = inject(FormBuilder).group({
    firstName: [this.item?.firstName ?? '', [Validators.required, Validators.maxLength(100)]],
    lastName: [this.item?.lastName ?? '', [Validators.required, Validators.maxLength(100)]],
    company: [this.item?.company ?? '', [Validators.required, Validators.maxLength(200)]],
    email: [this.item?.email ?? null, Validators.email],
    phone: [this.item?.phone ?? null],
    jobTitle: [this.item?.jobTitle ?? null],
    source: [this.item?.source ?? ('Web' as LeadSource)],
    status: [this.item?.status ?? ('New' as LeadStatus)],
    estimatedValue: [this.item?.estimatedValue ?? null as number | null, Validators.min(0)],
    description: [this.item?.description ?? null],
    ownerId: [this.item?.ownerId ?? null as string | null],
  });

  save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const body: LeadRequest = { ...v, firstName: v.firstName!, lastName: v.lastName!, company: v.company!, source: v.source!, status: v.status! };
    this.saving.set(true);
    (this.item ? this.api.leads.update(this.item.id, body) : this.api.leads.create(body)).subscribe({
      next: (saved) => {
        this.notify.success('Lead saved');
        this.ref.close(saved);
      },
      error: (err) => {
        this.saving.set(false);
        this.notify.formError(err);
      },
    });
  }
}
