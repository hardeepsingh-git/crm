import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { CrmApi } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { TemplateInfo, TenantItem } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { confirm } from '../../shared/confirm-dialog';
import { PASSWORD_HINT, strongPassword } from '../auth/change-password-dialog';
import { toKey } from '../admin/object-list';

@Component({
  selector: 'app-tenant-form-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>New tenant</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid" id="tenant-form" (ngSubmit)="save()">
        <mat-form-field appearance="outline">
          <mat-label>Organisation name</mat-label>
          <input matInput formControlName="name" maxlength="200" required (input)="suggest()" />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Workspace ID (slug)</mat-label>
          <input matInput formControlName="slug" maxlength="64" required />
          <mat-hint>Lowercase letters, digits and hyphens</mat-hint>
        </mat-form-field>
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Template</mat-label>
          <mat-select formControlName="template">
            @for (t of templates.value(); track t.key) {
              <mat-option [value]="t.key">{{ t.name }} &mdash; <span class="muted">{{ t.description }}</span></mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Currency</mat-label>
          <input matInput formControlName="currency" maxlength="3" required />
        </mat-form-field>
        <mat-checkbox formControlName="seedDemoData">Add sample data</mat-checkbox>
        <h3 class="span-2">First administrator</h3>
        <mat-form-field appearance="outline"><mat-label>First name</mat-label><input matInput formControlName="adminFirstName" required /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Last name</mat-label><input matInput formControlName="adminLastName" required /></mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Email</mat-label><input matInput type="email" formControlName="adminEmail" required />
          @if (form.controls.adminEmail.hasError('email')) { <mat-error>Invalid email</mat-error> }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Initial password</mat-label><input matInput type="password" formControlName="adminPassword" autocomplete="new-password" required />
          <mat-hint>{{ hint }}</mat-hint>
          @if (form.controls.adminPassword.hasError('weakPassword')) { <mat-error>{{ hint }}</mat-error> }
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="tenant-form" [disabled]="saving()">Create tenant</button>
    </mat-dialog-actions>
  `,
})
export class TenantFormDialog {
  private readonly ref = inject(MatDialogRef<TenantFormDialog, TenantItem>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly templates = httpResource<TemplateInfo[]>(() => '/api/platform/templates', { defaultValue: [] });
  readonly hint = PASSWORD_HINT;
  readonly saving = signal(false);

  readonly form = inject(FormBuilder).nonNullable.group({
    name: ['', Validators.required],
    slug: ['', [Validators.required, Validators.pattern(/^[a-z0-9][a-z0-9-]{1,62}$/)]],
    template: ['sales', Validators.required],
    currency: ['AUD', [Validators.required, Validators.pattern(/^[A-Za-z]{3}$/)]],
    seedDemoData: [false],
    adminFirstName: ['', Validators.required],
    adminLastName: ['', Validators.required],
    adminEmail: ['', [Validators.required, Validators.email]],
    adminPassword: ['', [Validators.required, strongPassword]],
  });

  suggest(): void {
    if (!this.form.controls.slug.dirty) this.form.controls.slug.setValue(toKey(this.form.controls.name.value).replace(/_/g, '-'));
  }

  save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    this.saving.set(true);
    this.api.createTenant({ ...v, currency: v.currency.toUpperCase() }).subscribe({
      next: (saved) => {
        this.notify.success(`Tenant "${saved.name}" created`);
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
  selector: 'app-tenant-list',
  imports: [DatePipe, MatTableModule, MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule],
  template: `
    <div class="page-header">
      <div><h1>Tenants</h1><div class="muted">Platform administration: each tenant is an isolated organisation with its own users, data and configuration.</div></div>
      <span class="spacer"></span>
      <button mat-flat-button (click)="create()"><mat-icon>add_business</mat-icon> New tenant</button>
    </div>
    <div class="table-wrap">
      @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      <table mat-table [dataSource]="data.value() ?? []">
        <ng-container matColumnDef="name"><th mat-header-cell *matHeaderCellDef>Organisation</th>
          <td mat-cell *matCellDef="let t"><strong>{{ t.name }}</strong>&nbsp;<span class="muted">({{ t.slug }})</span>
            @if (t.id === currentTenantId) { <span class="chip">Yours</span> }</td></ng-container>
        <ng-container matColumnDef="template"><th mat-header-cell *matHeaderCellDef>Template</th><td mat-cell *matCellDef="let t">{{ t.template }}</td></ng-container>
        <ng-container matColumnDef="users"><th mat-header-cell *matHeaderCellDef>Users</th><td mat-cell *matCellDef="let t">{{ t.userCount }}</td></ng-container>
        <ng-container matColumnDef="currency"><th mat-header-cell *matHeaderCellDef>Currency</th><td mat-cell *matCellDef="let t">{{ t.currency }}</td></ng-container>
        <ng-container matColumnDef="status"><th mat-header-cell *matHeaderCellDef>Status</th>
          <td mat-cell *matCellDef="let t"><span class="chip" [attr.data-v]="t.status === 'Active' ? 'Active' : 'Inactive'">{{ t.status }}</span></td></ng-container>
        <ng-container matColumnDef="createdAt"><th mat-header-cell *matHeaderCellDef>Created</th><td mat-cell *matCellDef="let t">{{ t.createdAt | date: 'mediumDate' }}</td></ng-container>
        <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let t" class="actions">
            @if (t.id !== currentTenantId) {
              @if (t.status === 'Active') {
                <button mat-icon-button (click)="setStatus(t, 'Suspended')" matTooltip="Suspend (users can't sign in)"><mat-icon>pause_circle</mat-icon></button>
              } @else {
                <button mat-icon-button (click)="setStatus(t, 'Active')" matTooltip="Reactivate"><mat-icon>play_circle</mat-icon></button>
                <button mat-icon-button (click)="remove(t)" matTooltip="Delete permanently"><mat-icon>delete_forever</mat-icon></button>
              }
            }
          </td></ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
      </table>
    </div>
  `,
  styles: `td.actions { text-align: right; white-space: nowrap; }`,
})
export class TenantList {
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  readonly currentTenantId = inject(AuthService).user()?.tenant.id;
  readonly columns = ['name', 'template', 'users', 'currency', 'status', 'createdAt', 'actions'];
  readonly data = httpResource<TenantItem[]>(() => '/api/platform/tenants');

  create(): void {
    this.dialog.open(TenantFormDialog, { width: '720px' }).afterClosed().subscribe((saved) => saved && this.data.reload());
  }

  setStatus(t: TenantItem, status: 'Active' | 'Suspended'): void {
    const apply = () =>
      this.api.updateTenant(t.id, { name: t.name, currency: t.currency, status }).subscribe(() => {
        this.notify.success(`${t.name} ${status === 'Active' ? 'reactivated' : 'suspended'}`);
        this.data.reload();
      });
    if (status === 'Active') {
      apply();
      return;
    }
    confirm(this.dialog, {
      title: `Suspend ${t.name}?`,
      message: 'Its users will be signed out and unable to sign in until the tenant is reactivated. No data is deleted.',
      confirmText: 'Suspend',
      danger: true,
    }).subscribe((ok) => ok && apply());
  }

  remove(t: TenantItem): void {
    confirm(this.dialog, {
      title: `Delete ${t.name} permanently?`,
      message: `All users, records and configuration of this tenant will be erased. This cannot be undone. Type the workspace ID "${t.slug}" to confirm.`,
      confirmText: 'Delete tenant',
      danger: true,
      input: { label: 'Workspace ID', required: true },
    }).subscribe((typed) => {
      if (typed !== t.slug) {
        if (typeof typed === 'string') this.notify.warn('Workspace ID did not match; nothing was deleted.');
        return;
      }
      this.api.deleteTenant(t.id).subscribe(() => {
        this.notify.success(`${t.name} deleted`);
        this.data.reload();
      });
    });
  }
}
