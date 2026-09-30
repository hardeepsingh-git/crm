import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { CrmApi } from '../../core/api';
import { LeadConvertResult, LeadDetail } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { EntityPicker } from '../../shared/entity-picker';
import { todayPlus } from '../../shared/list';

@Component({
  selector: 'app-lead-convert-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatRadioModule, EntityPicker],
  template: `
    <h2 mat-dialog-title>Convert lead</h2>
    <mat-dialog-content>
      <p>A contact will be created for <strong>{{ lead.fullName }}</strong>.</p>
      <form [formGroup]="form" id="convert-form" (ngSubmit)="convert()">
        <mat-radio-group formControlName="accountMode" class="radio-stack">
          <mat-radio-button value="new">Create new account "{{ lead.company }}"</mat-radio-button>
          <mat-radio-button value="existing">Attach to an existing account</mat-radio-button>
        </mat-radio-group>
        @if (form.controls.accountMode.value === 'existing') {
          <app-entity-picker label="Existing account" kind="accounts" formControlName="existingAccountId" [required]="true" />
        }
        <mat-checkbox formControlName="createOpportunity">Create an opportunity</mat-checkbox>
        @if (form.controls.createOpportunity.value) {
          <div class="form-grid">
            <mat-form-field appearance="outline" class="span-2">
              <mat-label>Opportunity name</mat-label><input matInput formControlName="opportunityName" maxlength="200" />
            </mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Amount</mat-label><input matInput type="number" min="0" formControlName="amount" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Expected close</mat-label><input matInput type="date" formControlName="expectedCloseDate" /></mat-form-field>
          </div>
        }
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="convert-form" [disabled]="saving()">Convert</button>
    </mat-dialog-actions>
  `,
  styles: `.radio-stack { display: flex; flex-direction: column; margin-bottom: 12px; }`,
})
export class LeadConvertDialog {
  readonly lead = inject<LeadDetail>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<LeadConvertDialog, LeadConvertResult>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly saving = signal(false);

  readonly form = inject(FormBuilder).group({
    accountMode: ['new' as 'new' | 'existing'],
    existingAccountId: [null as string | null],
    createOpportunity: [true],
    opportunityName: [`${this.lead.company} opportunity`],
    amount: [this.lead.estimatedValue ?? null as number | null, Validators.min(0)],
    expectedCloseDate: [todayPlus(30)],
  });

  convert(): void {
    const v = this.form.getRawValue();
    if (v.accountMode === 'existing' && !v.existingAccountId) {
      this.notify.warn('Select an existing account or choose to create a new one.');
      return;
    }
    this.saving.set(true);
    this.api
      .convertLead(this.lead.id, {
        existingAccountId: v.accountMode === 'existing' ? v.existingAccountId : null,
        createOpportunity: !!v.createOpportunity,
        opportunityName: v.opportunityName,
        amount: v.amount,
        expectedCloseDate: v.expectedCloseDate || null,
      })
      .subscribe({
        next: (result) => {
          this.notify.success('Lead converted');
          this.ref.close(result);
        },
        error: (err) => {
          this.saving.set(false);
          this.notify.formError(err);
        },
      });
  }
}
