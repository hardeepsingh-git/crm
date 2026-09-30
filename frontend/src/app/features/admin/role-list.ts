import { httpResource } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { CrmApi } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { MetadataService } from '../../core/metadata.service';
import { ADMIN_RESOURCES, Grant, PermissionScope, RECORD_ACTIONS, RecordAction, RoleItem, SCOPES } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { confirm } from '../../shared/confirm-dialog';
import { SettingsBack } from './settings-home';

type Cell = PermissionScope | 'Default';
type Matrix = Record<string, Record<RecordAction, Cell>>;

const SCOPE_HELP: Record<PermissionScope, string> = {
  None: 'No access',
  Own: 'Records the user owns',
  Group: 'Records owned by anyone in the user’s groups',
  All: 'Every record in the organisation',
};

@Component({
  selector: 'app-role-editor-dialog',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatTooltipModule],
  template: `
    <h2 mat-dialog-title>{{ role ? 'Edit role' : 'New role' }}</h2>
    <mat-dialog-content>
      @if (role?.isAdministrator) {
        <p class="banner">The Administrator role always has full access to everything and cannot be changed.</p>
      }
      <div class="form-grid">
        <mat-form-field appearance="outline">
          <mat-label>Name</mat-label>
          <input matInput [(ngModel)]="name" maxlength="100" required [disabled]="readOnly" />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Description</mat-label>
          <input matInput [(ngModel)]="description" maxlength="500" [disabled]="readOnly" />
        </mat-form-field>
      </div>

      <h3>Record access</h3>
      <p class="muted small">
        Scope: <strong>Own</strong> = records the user owns &middot; <strong>Group</strong> = records owned by members of the user's groups
        &middot; <strong>All</strong> = every record. "All objects" is the default for every object, including ones created later;
        set a specific object to override it (choose <strong>None</strong> to hide an object from this role). A user with several
        roles gets the widest access any of them grants.
      </p>
      <table class="matrix">
        <thead>
          <tr><th>Object</th>@for (a of actions; track a) { <th>{{ a }}</th> }</tr>
        </thead>
        <tbody>
          @for (row of rows(); track row.key) {
            <tr [class.wildcard]="row.key === '*'">
              <td>{{ row.label }}</td>
              @for (a of actions; track a) {
                <td>
                  <select [(ngModel)]="matrix[row.key][a]" [disabled]="readOnly" [attr.aria-label]="row.label + ' ' + a"
                    [attr.data-scope]="matrix[row.key][a]">
                    @if (row.key !== '*') { <option value="Default">Default ({{ matrix['*'][a] }})</option> }
                    @for (s of scopes; track s) { <option [value]="s" [title]="help[s]">{{ s }}</option> }
                  </select>
                </td>
              }
            </tr>
          }
        </tbody>
      </table>

      <h3>Administration</h3>
      <div class="admin-grid">
        @for (r of adminResources; track r.key) {
          <mat-checkbox [(ngModel)]="admin[r.key]" [disabled]="readOnly">{{ r.label }}</mat-checkbox>
        }
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ readOnly ? 'Close' : 'Cancel' }}</button>
      @if (!readOnly) {
        <button mat-flat-button (click)="save()" [disabled]="saving() || !name.trim()">Save</button>
      }
    </mat-dialog-actions>
  `,
  styles: `
    .matrix { width: 100%; border-collapse: collapse; margin-bottom: 16px; }
    .matrix th, .matrix td { padding: 6px 8px; border-bottom: 1px solid var(--mat-sys-outline-variant); text-align: left; }
    .matrix th { font-weight: 500; text-transform: capitalize; color: var(--mat-sys-on-surface-variant); }
    .matrix tr.wildcard td { font-weight: 600; background: var(--mat-sys-surface-container); }
    .matrix select { font: inherit; padding: 4px 6px; border-radius: 6px; border: 1px solid var(--mat-sys-outline-variant); background: var(--mat-sys-surface); color: inherit; min-width: 90px; }
    .matrix select[data-scope='All'] { border-color: #2e7d32; }
    .matrix select[data-scope='Group'] { border-color: #00838f; }
    .matrix select[data-scope='Own'] { border-color: #ef6c00; }
    .matrix select[data-scope='None'] { border-color: #c62828; }
    .matrix select[data-scope='Default'] { color: var(--mat-sys-on-surface-variant); }
    .admin-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 4px 16px; }
    .small { font-size: 13px; }
  `,
})
export class RoleEditorDialog {
  readonly role = inject<RoleItem | null>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<RoleEditorDialog, RoleItem>);
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  private readonly meta = inject(MetadataService);
  readonly actions = RECORD_ACTIONS;
  readonly scopes = SCOPES;
  readonly help = SCOPE_HELP;
  readonly adminResources = ADMIN_RESOURCES;
  readonly readOnly = !!this.role?.isAdministrator;
  readonly saving = signal(false);

  name = this.role?.name ?? '';
  description = this.role?.description ?? '';
  readonly rows = computed(() => [
    { key: '*', label: 'All objects' },
    ...this.meta.objects().map((o) => ({ key: o.key, label: o.pluralLabel + (o.isActive ? '' : ' (inactive)') })),
  ]);
  readonly matrix: Matrix = {};
  readonly admin: Record<string, boolean> = {};

  constructor() {
    const grants = this.role?.permissions ?? [];
    for (const row of this.rows()) {
      const fill: Cell = row.key === '*' ? 'None' : 'Default';
      this.matrix[row.key] = { read: fill, create: fill, update: fill, delete: fill };
    }
    for (const g of grants) {
      const adminResource = ADMIN_RESOURCES.find((r) => r.key === g.resource);
      if (adminResource) this.admin[g.resource] = g.scope !== 'None';
      else if (this.matrix[g.resource] && g.action in this.matrix[g.resource]) this.matrix[g.resource][g.action as RecordAction] = g.scope;
    }
  }

  save(): void {
    const permissions: Grant[] = [];
    for (const [resource, actions] of Object.entries(this.matrix)) {
      for (const [action, scope] of Object.entries(actions)) {
        // "*" None is the default; for objects, "Default" inherits "*" and an explicit None denies.
        if (scope === 'Default' || (resource === '*' && scope === 'None')) continue;
        permissions.push({ resource, action, scope });
      }
    }
    for (const r of ADMIN_RESOURCES) {
      if (this.admin[r.key]) permissions.push({ resource: r.key, action: r.action, scope: 'All' });
    }
    this.saving.set(true);
    this.api.saveRole(this.role?.id ?? null, { name: this.name.trim(), description: this.description.trim() || null, permissions }).subscribe({
      next: (saved) => {
        this.notify.success('Role saved');
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
  selector: 'app-role-list',
  imports: [MatTableModule, MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule, SettingsBack],
  template: `
    <div class="page-header">
      <div><app-settings-back /><h1>Roles &amp; permissions</h1></div>
      <span class="spacer"></span>
      <button mat-flat-button (click)="open(null)"><mat-icon>add</mat-icon> New role</button>
    </div>
    <p class="muted">A user's access is the combination of every role assigned to them directly or through their groups.</p>
    <div class="table-wrap">
      @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      <table mat-table [dataSource]="data.value() ?? []">
        <ng-container matColumnDef="name">
          <th mat-header-cell *matHeaderCellDef>Role</th>
          <td mat-cell *matCellDef="let r">
            <strong>{{ r.name }}</strong>
            @if (r.isAdministrator) { <span class="chip" data-v="Active">Full access</span> }
            @else if (r.isSystem) { <span class="chip">Built-in</span> }
            @if (r.description) { <div class="muted">{{ r.description }}</div> }
          </td>
        </ng-container>
        <ng-container matColumnDef="summary">
          <th mat-header-cell *matHeaderCellDef>Access</th>
          <td mat-cell *matCellDef="let r" class="muted">{{ summary(r) }}</td>
        </ng-container>
        <ng-container matColumnDef="usage">
          <th mat-header-cell *matHeaderCellDef>Used by</th>
          <td mat-cell *matCellDef="let r">{{ r.userCount }} users &middot; {{ r.groupCount }} groups</td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let r" class="actions">
            <button mat-icon-button (click)="open(r)" [attr.aria-label]="r.isAdministrator ? 'View' : 'Edit'"><mat-icon>{{ r.isAdministrator ? 'visibility' : 'edit' }}</mat-icon></button>
            @if (!r.isSystem) {
              <button mat-icon-button (click)="remove(r)" aria-label="Delete"><mat-icon>delete</mat-icon></button>
            }
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
      </table>
    </div>
  `,
  styles: `td.actions { text-align: right; white-space: nowrap; }`,
})
export class RoleList {
  private readonly api = inject(CrmApi);
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly meta = inject(MetadataService);
  readonly columns = ['name', 'summary', 'usage', 'actions'];
  readonly data = httpResource<RoleItem[]>(() => '/api/admin/roles');

  summary(r: RoleItem): string {
    if (r.isAdministrator) return 'Everything';
    const describe = (resource: string) => (resource === '*' ? 'all objects' : this.meta.label(resource, true));
    const reads = r.permissions.filter((g) => g.action === 'read' && !ADMIN_RESOURCES.some((a) => a.key === g.resource));
    const admin = r.permissions.filter((g) => ADMIN_RESOURCES.some((a) => a.key === g.resource)).map((g) => g.resource);
    const parts = reads.map((g) => (g.scope === 'None' ? `no ${describe(g.resource)}` : `${describe(g.resource)} (${g.scope})`));
    if (admin.length) parts.push(`admin: ${admin.join(', ')}`);
    return parts.join(', ') || 'No access';
  }

  open(role: RoleItem | null): void {
    this.dialog.open(RoleEditorDialog, { data: role, width: '860px', maxHeight: '90vh' }).afterClosed().subscribe((saved) => {
      if (!saved) return;
      this.data.reload();
      this.auth.reloadUser();
    });
  }

  remove(role: RoleItem): void {
    confirm(this.dialog, {
      title: 'Delete role?',
      message: `"${role.name}" will be removed from ${role.userCount} users and ${role.groupCount} groups.`,
      confirmText: 'Delete',
      danger: true,
    }).subscribe((ok) => {
      if (!ok) return;
      this.api.deleteRole(role.id).subscribe(() => {
        this.notify.success('Role deleted');
        this.data.reload();
        this.auth.reloadUser();
      });
    });
  }
}
