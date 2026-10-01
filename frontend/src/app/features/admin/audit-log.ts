import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { toSignal, toObservable } from '@angular/core/rxjs-interop';
import { debounceTime } from 'rxjs';
import { UserLookupService, stableValue, toParams } from '../../core/api';
import { MetadataService } from '../../core/metadata.service';
import { AuditAction, AuditItem, PagedResult } from '../../core/models';
import { inputValue } from '../../shared/list';
import { SettingsBack } from './settings-home';

@Component({
  selector: 'app-audit-log',
  imports: [DatePipe, RouterLink, MatFormFieldModule, MatIconModule, MatInputModule, MatPaginatorModule, MatProgressBarModule, MatSelectModule, MatTableModule, SettingsBack],
  template: `
    <div class="page-header"><div><app-settings-back /><h1>Audit log</h1></div></div>
    <div class="filters">
      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="search">
        <mat-label>Search record name</mat-label>
        <input matInput [value]="search()" (input)="search.set(inputValue($event)); page.set(0)" />
        <mat-icon matSuffix>search</mat-icon>
      </mat-form-field>
      <mat-form-field appearance="outline" subscriptSizing="dynamic">
        <mat-label>Object</mat-label>
        <mat-select [value]="entityType()" (selectionChange)="entityType.set($event.value); page.set(0)">
          <mat-option [value]="null">All</mat-option>
          @for (o of meta.objects(); track o.key) { <mat-option [value]="o.key">{{ o.label }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field appearance="outline" subscriptSizing="dynamic">
        <mat-label>Action</mat-label>
        <mat-select [value]="action()" (selectionChange)="action.set($event.value); page.set(0)">
          <mat-option [value]="null">All</mat-option>
          @for (a of actions; track a) { <mat-option [value]="a">{{ a }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field appearance="outline" subscriptSizing="dynamic">
        <mat-label>User</mat-label>
        <mat-select [value]="userId()" (selectionChange)="userId.set($event.value); page.set(0)">
          <mat-option [value]="null">Anyone</mat-option>
          @for (u of lookup.users.value(); track u.id) { <mat-option [value]="u.id">{{ u.fullName }}</mat-option> }
        </mat-select>
      </mat-form-field>
    </div>
    <div class="table-wrap">
      @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="timestamp"><th mat-header-cell *matHeaderCellDef>When</th>
          <td mat-cell *matCellDef="let a">{{ a.timestamp | date: 'medium' }}</td></ng-container>
        <ng-container matColumnDef="user"><th mat-header-cell *matHeaderCellDef>Who</th>
          <td mat-cell *matCellDef="let a">{{ a.userName ?? 'System' }}</td></ng-container>
        <ng-container matColumnDef="action"><th mat-header-cell *matHeaderCellDef>Action</th>
          <td mat-cell *matCellDef="let a"><span class="chip" [attr.data-v]="chip(a.action)">{{ a.action }}</span></td></ng-container>
        <ng-container matColumnDef="record"><th mat-header-cell *matHeaderCellDef>Record</th>
          <td mat-cell *matCellDef="let a">
            <span class="muted">{{ meta.label(a.entityType) }}:</span>
            @if (linkable(a)) {
              <a [routerLink]="meta.route(a.entityType, a.entityId)">{{ a.entityName ?? a.entityId }}</a>
            } @else { {{ a.entityName ?? a.entityId }} }
          </td></ng-container>
        <ng-container matColumnDef="changes"><th mat-header-cell *matHeaderCellDef>Changes</th>
          <td mat-cell *matCellDef="let a" class="changes">
            @for (c of changes(a); track c) { <div>{{ c }}</div> }
          </td></ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
        <tr class="mat-mdc-row" *matNoDataRow><td class="mat-mdc-cell empty" colspan="5">No entries.</td></tr>
      </table>
      <mat-paginator [length]="total()" [pageIndex]="page()" [pageSize]="pageSize()" [pageSizeOptions]="[25, 50, 100]" (page)="onPage($event)" />
    </div>
  `,
  styles: `td.changes { font-size: 12px; max-width: 480px; } td.changes div { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }`,
})
export class AuditLog {
  readonly meta = inject(MetadataService);
  readonly lookup = inject(UserLookupService);
  readonly inputValue = inputValue;
  readonly actions: AuditAction[] = ['Created', 'Updated', 'Deleted', 'Exported', 'Erased'];
  readonly columns = ['timestamp', 'user', 'action', 'record', 'changes'];
  readonly search = signal('');
  readonly entityType = signal<string | null>(null);
  readonly action = signal<AuditAction | null>(null);
  readonly userId = signal<string | null>(null);
  readonly page = signal(0);
  readonly pageSize = signal(50);
  private readonly debouncedSearch = toSignal(toObservable(this.search).pipe(debounceTime(300)), { initialValue: '' });

  readonly data = httpResource<PagedResult<AuditItem>>(() => ({
    url: '/api/admin/audit',
    params: toParams({
      search: this.debouncedSearch(),
      entityType: this.entityType(),
      action: this.action(),
      userId: this.userId(),
      page: this.page() + 1,
      pageSize: this.pageSize(),
    }),
  }));
  private readonly value = stableValue(this.data);
  readonly rows = computed(() => this.value()?.items ?? []);
  readonly total = computed(() => this.value()?.totalCount ?? 0);

  chip(action: AuditAction): string {
    return action === 'Created' ? 'Active' : action === 'Deleted' || action === 'Erased' ? 'Inactive' : 'Normal';
  }

  /** Deleted, erased and exported entries have no record to open. */
  linkable(a: AuditItem): boolean {
    return (a.action === 'Created' || a.action === 'Updated') && !!this.meta.object(a.entityType);
  }

  changes(a: AuditItem): string[] {
    if (a.action === 'Erased') {
      const c = a.changes as unknown as Record<string, string | number>;
      return [`Reason: ${c['reason'] ?? 'not given'}`, `${c['applications'] ?? 0} applications, ${c['files'] ?? 0} files, ${c['emails'] ?? 0} emails removed`];
    }
    if (a.action === 'Exported') {
      const filters = Object.entries((a.changes as unknown as { filters?: Record<string, string> }).filters ?? {});
      return filters.length ? filters.map(([k, v]) => `${k} = ${v}`) : ['No filters'];
    }
    if (a.action !== 'Updated') return [];
    const show = (v: unknown) => (v === null || v === undefined || v === '' ? '∅' : Array.isArray(v) ? v.join(', ') : String(v));
    return Object.entries(a.changes).map(([k, [from, to]]) => `${k}: ${show(from)} → ${show(to)}`);
  }

  onPage(e: PageEvent): void {
    this.page.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
  }
}
