import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { CrmApi } from '../../core/api';
import { ACTIVITY_TYPES, ActivityItem, ActivityLinks, ActivityPriority, ActivityRequest, ActivityType, PRIORITIES } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { EntityPicker } from '../../shared/entity-picker';
import { fromLocalInput, toLocalInput } from '../../shared/list';
import { OwnerField } from '../../shared/owner-field';

export interface ActivityFormData {
  item?: ActivityItem;
  links?: ActivityLinks;
  type?: ActivityType;
}

@Component({
  selector: 'app-activity-form-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, EntityPicker, OwnerField],
  template: `
    <h2 mat-dialog-title>{{ data.item ? 'Edit activity' : 'New activity' }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid" id="activity-form" (ngSubmit)="save()">
        <mat-form-field appearance="outline">
          <mat-label>Type</mat-label>
          <mat-select formControlName="type">
            @for (t of types; track t) {
              <mat-option [value]="t">{{ t }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Priority</mat-label>
          <mat-select formControlName="priority">
            @for (p of priorities; track p) {
              <mat-option [value]="p">{{ p }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Subject</mat-label>
          <input matInput formControlName="subject" maxlength="200" required />
          @if (form.controls.subject.hasError('required')) {
            <mat-error>Subject is required</mat-error>
          }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Due</mat-label>
          <input matInput type="datetime-local" formControlName="dueAt" />
        </mat-form-field>
        <app-owner-field [control]="form.controls.ownerId" />
        <app-entity-picker class="span-2" label="Account" kind="accounts" formControlName="accountId" />
        <app-entity-picker label="Contact" kind="contacts" formControlName="contactId" />
        <app-entity-picker label="Opportunity" kind="opportunities" formControlName="opportunityId" />
        @if (form.controls.leadId.value) {
          <app-entity-picker class="span-2" label="Lead" kind="leads" formControlName="leadId" />
        }
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Description</mat-label>
          <textarea matInput rows="4" formControlName="description" maxlength="4000"></textarea>
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="activity-form" [disabled]="saving()">Save</button>
    </mat-dialog-actions>
  `,
})
export class ActivityFormDialog {
  readonly data = inject<ActivityFormData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<ActivityFormDialog, ActivityItem>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly types = ACTIVITY_TYPES;
  readonly priorities = PRIORITIES;
  readonly saving = signal(false);

  private readonly item = this.data.item;
  private readonly links = this.data.links ?? {};
  readonly form = inject(FormBuilder).group({
    type: [this.item?.type ?? this.data.type ?? ('Task' as ActivityType), Validators.required],
    subject: [this.item?.subject ?? '', [Validators.required, Validators.maxLength(200)]],
    priority: [this.item?.priority ?? ('Normal' as ActivityPriority)],
    dueAt: [toLocalInput(this.item?.dueAt ?? null)],
    description: [this.item?.description ?? null as string | null],
    accountId: [this.item?.accountId ?? this.links.accountId ?? null],
    contactId: [this.item?.contactId ?? this.links.contactId ?? null],
    leadId: [this.item?.leadId ?? this.links.leadId ?? null],
    opportunityId: [this.item?.opportunityId ?? this.links.opportunityId ?? null],
    ownerId: [this.item?.ownerId ?? null as string | null],
  });

  save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const body: ActivityRequest = { ...v, type: v.type!, subject: v.subject!, priority: v.priority!, dueAt: fromLocalInput(v.dueAt) };
    this.saving.set(true);
    const request = this.item ? this.api.activities.update(this.item.id, body) : this.api.activities.create(body);
    request.subscribe({
      next: (saved) => {
        this.notify.success('Activity saved');
        this.ref.close(saved);
      },
      error: (err) => {
        this.saving.set(false);
        this.notify.formError(err);
      },
    });
  }
}
