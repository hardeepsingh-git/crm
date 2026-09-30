import { httpResource } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { CrmApi, UserLookupService } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { GroupItem, NamedRef, RoleItem } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { confirm } from '../../shared/confirm-dialog';
import { SettingsBack } from './settings-home';

@Component({
  selector: 'app-group-form-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ item ? 'Edit group' : 'New group' }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid" id="group-form" (ngSubmit)="save()">
        <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput formControlName="name" maxlength="100" required /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Description</mat-label><input matInput formControlName="description" maxlength="500" /></mat-form-field>
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Members</mat-label>
          <mat-select formControlName="memberIds" multiple>
            @for (u of lookup.users.value(); track u.id) { <mat-option [value]="u.id">{{ u.fullName }} ({{ u.email }})</mat-option> }
          </mat-select>
          <mat-hint>Members can see each other's records wherever a role grants "Group" scope</mat-hint>
        </mat-form-field>
        <mat-form-field appearance="outline" class="span-2">
          <mat-label>Roles granted to every member</mat-label>
          <mat-select formControlName="roleIds" multiple>
            @for (r of roles.value(); track r.id) {
              <mat-option [value]="r.id" [disabled]="r.isAdministrator && !auth.isAdmin()">{{ r.name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button type="submit" form="group-form" [disabled]="saving()">Save</button>
    </mat-dialog-actions>
  `,
})
export class GroupFormDialog {
  readonly item = inject<GroupItem | null>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<GroupFormDialog, GroupItem>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly auth = inject(AuthService);
  readonly lookup = inject(UserLookupService);
  readonly roles = httpResource<RoleItem[]>(() => '/api/admin/roles', { defaultValue: [] });
  readonly saving = signal(false);

  readonly form = inject(FormBuilder).nonNullable.group({
    name: [this.item?.name ?? '', [Validators.required, Validators.maxLength(100)]],
    description: [this.item?.description ?? ''],
    memberIds: [this.item?.members.map((m) => m.id) ?? ([] as string[])],
    roleIds: [this.item?.roles.map((r) => r.id) ?? ([] as string[])],
  });

  save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    this.saving.set(true);
    this.api.saveGroup(this.item?.id ?? null, { ...v, name: v.name.trim(), description: v.description.trim() || null }).subscribe({
      next: (saved) => {
        this.notify.success('Group saved');
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
  selector: 'app-group-list',
  imports: [MatTableModule, MatButtonModule, MatIconModule, MatProgressBarModule, SettingsBack],
  template: `
    <div class="page-header">
      <div><app-settings-back /><h1>Groups</h1></div>
      <span class="spacer"></span>
      <button mat-flat-button (click)="open(null)"><mat-icon>add</mat-icon> New group</button>
    </div>
    <p class="muted">Groups model teams, regions or departments. Members inherit the group's roles and share records under "Group" scope.</p>
    <div class="table-wrap">
      @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      <table mat-table [dataSource]="data.value() ?? []">
        <ng-container matColumnDef="name">
          <th mat-header-cell *matHeaderCellDef>Group</th>
          <td mat-cell *matCellDef="let g"><strong>{{ g.name }}</strong> @if (g.description) { <div class="muted">{{ g.description }}</div> }</td>
        </ng-container>
        <ng-container matColumnDef="members">
          <th mat-header-cell *matHeaderCellDef>Members</th>
          <td mat-cell *matCellDef="let g">{{ names(g.members) || '-' }}</td>
        </ng-container>
        <ng-container matColumnDef="roles">
          <th mat-header-cell *matHeaderCellDef>Roles</th>
          <td mat-cell *matCellDef="let g">{{ names(g.roles) || '-' }}</td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let g" class="actions">
            <button mat-icon-button (click)="open(g)" aria-label="Edit"><mat-icon>edit</mat-icon></button>
            <button mat-icon-button (click)="remove(g)" aria-label="Delete"><mat-icon>delete</mat-icon></button>
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
        <tr class="mat-mdc-row" *matNoDataRow><td class="mat-mdc-cell empty" colspan="4">No groups yet.</td></tr>
      </table>
    </div>
  `,
  styles: `td.actions { text-align: right; white-space: nowrap; }`,
})
export class GroupList {
  private readonly api = inject(CrmApi);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  readonly columns = ['name', 'members', 'roles', 'actions'];
  readonly data = httpResource<GroupItem[]>(() => '/api/admin/groups');

  names(refs: NamedRef[]): string {
    return refs.map((r) => r.name).join(', ');
  }

  open(group: GroupItem | null): void {
    this.dialog.open(GroupFormDialog, { data: group, width: '640px' }).afterClosed().subscribe((saved) => {
      if (!saved) return;
      this.data.reload();
      this.auth.reloadUser();
    });
  }

  remove(group: GroupItem): void {
    confirm(this.dialog, {
      title: 'Delete group?',
      message: `"${group.name}" will be deleted. Its members lose the roles granted through it.`,
      confirmText: 'Delete',
      danger: true,
    }).subscribe((ok) => {
      if (!ok) return;
      this.api.deleteGroup(group.id).subscribe(() => {
        this.notify.success('Group deleted');
        this.data.reload();
        this.auth.reloadUser();
      });
    });
  }
}
