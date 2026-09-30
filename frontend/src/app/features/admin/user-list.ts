import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, input, linkedSignal, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { CrmApi, UserLookupService } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { GroupItem, NamedRef, RegistrationStatus, RoleItem, UserItem } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { confirm } from '../../shared/confirm-dialog';
import { PASSWORD_HINT, strongPassword } from '../auth/change-password-dialog';
import { SettingsBack } from './settings-home';

@Component({
  selector: 'app-user-form-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ item ? 'Edit user' : 'New user' }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid" id="user-form" (ngSubmit)="save()">
        <mat-form-field appearance="outline"><mat-label>First name</mat-label><input matInput formControlName="firstName" required /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Last name</mat-label><input matInput formControlName="lastName" required /></mat-form-field>
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Email</mat-label><input matInput type="email" formControlName="email" required />
          @if (form.controls.email.hasError('email')) { <mat-error>Invalid email</mat-error> }
        </mat-form-field>
        @if (!item) {
          <mat-form-field appearance="outline" class="span-2">
            <mat-label>Initial password</mat-label><input matInput type="password" formControlName="password" autocomplete="new-password" required />
            <mat-hint>{{ hint }}</mat-hint>
            @if (form.controls.password.hasError('weakPassword')) { <mat-error>{{ hint }}</mat-error> }
          </mat-form-field>
        }
        <mat-form-field appearance="outline">
          <mat-label>Roles</mat-label>
          <mat-select formControlName="roleIds" multiple>
            @for (r of roles.value(); track r.id) {
              <mat-option [value]="r.id" [disabled]="r.isAdministrator && !auth.isAdmin()">{{ r.name }}</mat-option>
            }
          </mat-select>
          <mat-hint>Assigned directly to this user</mat-hint>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Groups</mat-label>
          <mat-select formControlName="groupIds" multiple>
            @for (g of groups.value(); track g.id) { <mat-option [value]="g.id">{{ g.name }}</mat-option> }
          </mat-select>
          <mat-hint>Users also get every role of their groups</mat-hint>
        </mat-form-field>
        @if (item) {
          @if (item.registrationStatus === 'Approved') {
            <mat-checkbox formControlName="isActive">Active</mat-checkbox>
          } @else {
            <p class="muted span-2">This is a pending self-registration. Use <strong>Approve</strong> in the user list to activate it.</p>
          }
        }
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="user-form" [disabled]="saving()">Save</button>
    </mat-dialog-actions>
  `,
})
export class UserFormDialog {
  readonly item = inject<UserItem | null>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<UserFormDialog, UserItem>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly auth = inject(AuthService);
  readonly roles = httpResource<RoleItem[]>(() => '/api/admin/roles', { defaultValue: [] });
  readonly groups = httpResource<GroupItem[]>(() => '/api/admin/groups', { defaultValue: [] });
  readonly hint = PASSWORD_HINT;
  readonly saving = signal(false);

  readonly form = inject(FormBuilder).nonNullable.group({
    firstName: [this.item?.firstName ?? '', Validators.required],
    lastName: [this.item?.lastName ?? '', Validators.required],
    email: [{ value: this.item?.email ?? '', disabled: !!this.item }, [Validators.required, Validators.email]],
    password: ['', this.item ? [] : [Validators.required, strongPassword]],
    roleIds: [this.item?.roles.map((r) => r.id) ?? ([] as string[])],
    groupIds: [this.item?.groups.map((g) => g.id) ?? ([] as string[])],
    isActive: [this.item?.isActive ?? true],
  });

  save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    this.saving.set(true);
    const assignments = { roleIds: v.roleIds, groupIds: v.groupIds };
    const request = this.item
      ? this.api.updateUser(this.item.id, { firstName: v.firstName, lastName: v.lastName, isActive: v.isActive, ...assignments })
      : this.api.createUser({ email: v.email, firstName: v.firstName, lastName: v.lastName, password: v.password, ...assignments });
    request.subscribe({
      next: (saved) => {
        this.notify.success('User saved');
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
  selector: 'app-approve-user-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Approve {{ user.fullName }}</h2>
    <mat-dialog-content>
      <dl class="fields">
        <dt>Email</dt><dd>{{ user.email }} @if (user.emailConfirmed) { <mat-icon inline title="Verified">verified</mat-icon> } @else { <span class="muted">(not verified)</span> }</dd>
        <dt>Mobile</dt><dd>{{ user.phoneNumber ?? '-' }} @if (user.phoneNumber && user.phoneNumberConfirmed) { <mat-icon inline title="Verified">verified</mat-icon> } @else if (user.phoneNumber) { <span class="muted">(not verified)</span> }</dd>
      </dl>
      @if (user.registrationStatus === 'PendingVerification') {
        <p class="warn">This person hasn't finished verifying their details. Approve only if you've confirmed their identity another way.</p>
      }
      <form [formGroup]="form" class="form-grid" id="approve-form" (ngSubmit)="approve()">
        <mat-form-field appearance="outline">
          <mat-label>Roles</mat-label>
          <mat-select formControlName="roleIds" multiple>
            @for (r of roles.value(); track r.id) {
              <mat-option [value]="r.id" [disabled]="r.isAdministrator && !auth.isAdmin()">{{ r.name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Groups</mat-label>
          <mat-select formControlName="groupIds" multiple>
            @for (g of groups.value(); track g.id) { <mat-option [value]="g.id">{{ g.name }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <p class="muted span-2">Give at least one role, directly or through a group. The user can sign in as soon as you approve.</p>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="approve-form" [disabled]="saving()">Approve and activate</button>
    </mat-dialog-actions>
  `,
  styles: `.warn { color: var(--mat-sys-error); }`,
})
export class ApproveUserDialog {
  readonly user = inject<UserItem>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<ApproveUserDialog, UserItem>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly auth = inject(AuthService);
  readonly roles = httpResource<RoleItem[]>(() => '/api/admin/roles', { defaultValue: [] });
  readonly groups = httpResource<GroupItem[]>(() => '/api/admin/groups', { defaultValue: [] });
  readonly saving = signal(false);
  readonly form = inject(FormBuilder).nonNullable.group({
    roleIds: [[] as string[]],
    groupIds: [[] as string[]],
  });

  approve(): void {
    const v = this.form.getRawValue();
    if (v.roleIds.length === 0 && v.groupIds.length === 0) {
      this.notify.warn('Choose at least one role or group.');
      return;
    }
    this.saving.set(true);
    this.api.approveUser(this.user.id, v).subscribe({
      next: (saved) => {
        this.notify.success(`${saved.fullName} approved`);
        this.ref.close(saved);
      },
      error: (err) => {
        this.saving.set(false);
        this.notify.formError(err);
      },
    });
  }
}

const STATUS_LABELS: Record<RegistrationStatus, string> = {
  Approved: 'Approved',
  PendingVerification: 'Verifying',
  PendingApproval: 'Awaiting approval',
  Rejected: 'Rejected',
};

@Component({
  selector: 'app-user-list',
  imports: [DatePipe, MatTableModule, MatButtonModule, MatButtonToggleModule, MatIconModule, MatProgressBarModule, MatTooltipModule, SettingsBack],
  template: `
    <div class="page-header">
      <div><app-settings-back /><h1>Users</h1></div>
      <span class="spacer"></span>
      <button mat-flat-button (click)="create()"><mat-icon>person_add</mat-icon> New user</button>
    </div>
    <div class="filters">
      <mat-button-toggle-group [value]="view()" (change)="view.set($event.value)" hideSingleSelectionIndicator aria-label="User list">
        <mat-button-toggle value="all">All users</mat-button-toggle>
        <mat-button-toggle value="pending">Awaiting approval ({{ pendingCount() }})</mat-button-toggle>
      </mat-button-toggle-group>
    </div>
    <div class="table-wrap">
      @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="name"><th mat-header-cell *matHeaderCellDef>Name</th>
          <td mat-cell *matCellDef="let u">{{ u.fullName }} @if (u.isPlatformAdmin) { <span class="chip" title="Platform administrator">Platform</span> }</td></ng-container>
        <ng-container matColumnDef="email"><th mat-header-cell *matHeaderCellDef>Email</th><td mat-cell *matCellDef="let u">{{ u.email }}</td></ng-container>
        <ng-container matColumnDef="roles"><th mat-header-cell *matHeaderCellDef>Roles</th><td mat-cell *matCellDef="let u">{{ names(u.roles) }}</td></ng-container>
        <ng-container matColumnDef="groups"><th mat-header-cell *matHeaderCellDef>Groups</th><td mat-cell *matCellDef="let u">{{ names(u.groups) }}</td></ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>Status</th>
          <td mat-cell *matCellDef="let u">
            @if (u.registrationStatus === 'Approved') {
              <span class="chip" [attr.data-v]="u.isActive ? 'Active' : 'Inactive'">{{ u.isActive ? 'Active' : 'Inactive' }}</span>
            } @else {
              <span class="chip" [attr.data-v]="u.registrationStatus === 'Rejected' ? 'Inactive' : 'Qualified'">{{ statusLabel(u) }}</span>
            }
            @if (isLocked(u)) { <span class="chip" data-v="Locked">Locked</span> }
          </td>
        </ng-container>
        <ng-container matColumnDef="createdAt"><th mat-header-cell *matHeaderCellDef>Created</th><td mat-cell *matCellDef="let u">{{ u.createdAt | date: 'mediumDate' }}</td></ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let u" class="actions">
            @if (u.registrationStatus !== 'Approved') {
              <button mat-stroked-button (click)="approve(u)"><mat-icon>how_to_reg</mat-icon> Approve</button>
              @if (u.registrationStatus !== 'Rejected') {
                <button mat-icon-button (click)="reject(u)" matTooltip="Reject registration" aria-label="Reject registration"><mat-icon>block</mat-icon></button>
              }
            }
            <button mat-icon-button (click)="edit(u)" aria-label="Edit user"><mat-icon>edit</mat-icon></button>
            <button mat-icon-button (click)="resetPassword(u)" aria-label="Reset password"><mat-icon>lock_reset</mat-icon></button>
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
        <tr class="mat-mdc-row" *matNoDataRow><td class="mat-mdc-cell empty" [attr.colspan]="columns.length">{{ view() === 'pending' ? 'No registrations are waiting.' : 'No users.' }}</td></tr>
      </table>
    </div>
  `,
  styles: `td.actions { text-align: right; white-space: nowrap; }`,
})
export class UserList {
  /** Bound from ?tab=pending (the approval notification links here). */
  readonly tab = input<string>();
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly lookup = inject(UserLookupService);
  readonly auth = inject(AuthService);
  readonly columns = ['name', 'email', 'roles', 'groups', 'status', 'createdAt', 'actions'];
  readonly view = linkedSignal<'all' | 'pending'>(() => (this.tab() === 'pending' ? 'pending' : 'all'));
  readonly data = httpResource<UserItem[]>(() => '/api/users');
  private readonly all = computed(() => (this.data.hasValue() ? this.data.value() : []));
  private readonly pending = computed(() =>
    this.all().filter((u) => u.registrationStatus === 'PendingApproval' || u.registrationStatus === 'PendingVerification'),
  );
  readonly pendingCount = computed(() => this.pending().length);
  readonly rows = computed(() => (this.view() === 'pending' ? this.pending() : this.all()));

  statusLabel(u: UserItem): string {
    return STATUS_LABELS[u.registrationStatus];
  }

  approve(u: UserItem): void {
    this.dialog.open(ApproveUserDialog, { data: u, width: '560px' }).afterClosed().subscribe((saved) => {
      if (!saved) return;
      this.data.reload();
      this.lookup.users.reload();
    });
  }

  reject(u: UserItem): void {
    confirm(this.dialog, {
      title: `Reject ${u.fullName}?`,
      message: 'They will not be able to sign in. You can still approve them later from the All users list.',
      confirmText: 'Reject',
      danger: true,
    }).subscribe((ok) => {
      if (!ok) return;
      this.api.rejectUser(u.id).subscribe(() => {
        this.notify.success('Registration rejected');
        this.data.reload();
      });
    });
  }

  names(refs: NamedRef[]): string {
    return refs.map((r) => r.name).join(', ');
  }

  isLocked(u: UserItem): boolean {
    return !!u.lockoutEnd && new Date(u.lockoutEnd) > new Date();
  }

  create(): void {
    this.open(null);
  }

  edit(u: UserItem): void {
    this.open(u);
  }

  resetPassword(u: UserItem): void {
    confirm(this.dialog, {
      title: `Reset password for ${u.fullName}`,
      message: 'The user will be signed out of all sessions and must use the new password.',
      confirmText: 'Reset password',
      input: { label: 'New password', type: 'password', required: true, hint: PASSWORD_HINT },
    }).subscribe((password) => {
      if (typeof password !== 'string') return;
      this.api.resetPassword(u.id, password).subscribe({
        next: () => {
          this.notify.success('Password reset');
          this.data.reload();
        },
        error: (err) => this.notify.formError(err),
      });
    });
  }

  private open(item: UserItem | null): void {
    this.dialog.open(UserFormDialog, { data: item, width: '560px' }).afterClosed().subscribe((saved) => {
      if (!saved) return;
      this.data.reload();
      this.lookup.users.reload();
      if (saved.id === this.auth.user()?.id) this.auth.reloadUser();
    });
  }
}
