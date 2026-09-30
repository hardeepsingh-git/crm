import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, input, linkedSignal, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSortModule } from '@angular/material/sort';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { CrmApi, stableValue } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { ACTIVITY_TYPES, ActivityItem, ActivityType, PagedResult } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { ACTIVITY_ICONS, isOverdue } from '../../shared/activity-panel';
import { confirm } from '../../shared/confirm-dialog';
import { ListState, inputValue } from '../../shared/list';
import { ActivityFormDialog } from './activity-form-dialog';

type StatusFilter = 'Open' | 'Overdue' | 'Completed' | null;

@Component({
  selector: 'app-activity-list',
  imports: [DatePipe, RouterLink, MatTableModule, MatPaginatorModule, MatSortModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatIconModule, MatButtonModule, MatButtonToggleModule, MatCheckboxModule, MatSlideToggleModule, MatProgressBarModule, MatTooltipModule],
  template: `
    <div class="page-header">
      <h1>Activities</h1>
      <span class="spacer"></span>
      <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> New activity</button>
    </div>
    <div class="filters">
      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="search">
        <mat-label>Search subject</mat-label>
        <input matInput [value]="state.search()" (input)="state.setSearch(inputValue($event))" />
        <mat-icon matSuffix>search</mat-icon>
      </mat-form-field>
      <mat-button-toggle-group [value]="statusFilter()" (change)="statusFilter.set($event.value); state.resetPage()" hideSingleSelectionIndicator>
        <mat-button-toggle value="Open">Open</mat-button-toggle>
        <mat-button-toggle value="Overdue">Overdue</mat-button-toggle>
        <mat-button-toggle value="Completed">Completed</mat-button-toggle>
        <mat-button-toggle [value]="null">All</mat-button-toggle>
      </mat-button-toggle-group>
      <mat-form-field appearance="outline" subscriptSizing="dynamic">
        <mat-label>Type</mat-label>
        <mat-select [value]="type()" (selectionChange)="type.set($event.value); state.resetPage()">
          <mat-option [value]="null">All</mat-option>
          @for (t of types; track t) { <mat-option [value]="t">{{ t }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-slide-toggle [checked]="state.mine()" (change)="state.setMine($event.checked)">Mine</mat-slide-toggle>
    </div>
    <div class="table-wrap">
      @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      <table mat-table [dataSource]="rows()" matSort (matSortChange)="state.onSort($event)">
        <ng-container matColumnDef="done">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let a">
            @if (a.type !== 'Note') {
              <mat-checkbox [checked]="!!a.completedAt" [disabled]="!auth.canModify(a.ownerId)" (change)="toggle(a, $event.checked)"
                [matTooltip]="a.completedAt ? 'Reopen' : 'Mark complete'" />
            }
          </td>
        </ng-container>
        <ng-container matColumnDef="subject">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Subject</th>
          <td mat-cell *matCellDef="let a" [class.done]="a.completedAt">
            <mat-icon inline class="muted">{{ iconFor(a) }}</mat-icon> {{ a.subject }}
          </td>
        </ng-container>
        <ng-container matColumnDef="dueAt">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Due</th>
          <td mat-cell *matCellDef="let a" [class.overdue]="overdue(a)">{{ a.dueAt | date: 'd MMM y, h:mm a' }}</td>
        </ng-container>
        <ng-container matColumnDef="priority">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Priority</th>
          <td mat-cell *matCellDef="let a"><span class="chip" [attr.data-v]="a.priority">{{ a.priority }}</span></td>
        </ng-container>
        <ng-container matColumnDef="related">
          <th mat-header-cell *matHeaderCellDef>Related to</th>
          <td mat-cell *matCellDef="let a">
            @if (a.opportunityId) { <a [routerLink]="['/opportunities', a.opportunityId]">{{ a.opportunityName }}</a> }
            @else if (a.contactId) { <a [routerLink]="['/contacts', a.contactId]">{{ a.contactName }}</a> }
            @else if (a.leadId) { <a [routerLink]="['/leads', a.leadId]">{{ a.leadName }}</a> }
            @else if (a.accountId) { <a [routerLink]="['/accounts', a.accountId]">{{ a.accountName }}</a> }
          </td>
        </ng-container>
        <ng-container matColumnDef="owner">
          <th mat-header-cell *matHeaderCellDef>Owner</th>
          <td mat-cell *matCellDef="let a">{{ a.ownerName }}</td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let a" class="actions">
            @if (auth.canModify(a.ownerId)) {
              <button mat-icon-button (click)="edit(a)" aria-label="Edit"><mat-icon>edit</mat-icon></button>
              <button mat-icon-button (click)="remove(a)" aria-label="Delete"><mat-icon>delete</mat-icon></button>
            }
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
        <tr class="mat-mdc-row" *matNoDataRow><td class="mat-mdc-cell empty" [attr.colspan]="columns.length">No activities found.</td></tr>
      </table>
      <mat-paginator [length]="total()" [pageIndex]="state.page()" [pageSize]="state.pageSize()" [pageSizeOptions]="[10, 20, 50, 100]"
        (page)="state.onPage($event)" />
    </div>
  `,
  styles: `
    td.done { text-decoration: line-through; opacity: .7; }
    td.overdue { color: var(--mat-sys-error); font-weight: 500; }
    td.actions { white-space: nowrap; text-align: right; }
  `,
})
export class ActivityList {
  /** Bound from the `?status=` query param (e.g. dashboard "Overdue" link). */
  readonly status = input<StatusFilter>();
  readonly auth = inject(AuthService);
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  readonly overdue = isOverdue;
  readonly inputValue = inputValue;
  readonly types = ACTIVITY_TYPES;
  readonly state = new ListState();
  readonly type = signal<ActivityType | null>(null);
  readonly statusFilter = linkedSignal<StatusFilter>(() => this.status() ?? 'Open');
  readonly columns = ['done', 'subject', 'dueAt', 'priority', 'related', 'owner', 'actions'];

  readonly data = httpResource<PagedResult<ActivityItem>>(() => ({
    url: '/api/activities',
    params: this.state.params({ status: this.statusFilter(), type: this.type() }),
  }));
  private readonly value = stableValue(this.data);
  readonly rows = computed(() => this.value()?.items ?? []);
  readonly total = computed(() => this.value()?.totalCount ?? 0);

  create(): void {
    this.dialog.open(ActivityFormDialog, { data: {}, width: '640px' }).afterClosed().subscribe((s) => s && this.data.reload());
  }

  iconFor(a: ActivityItem): string {
    return ACTIVITY_ICONS[a.type];
  }

  edit(item: ActivityItem): void {
    this.dialog.open(ActivityFormDialog, { data: { item }, width: '640px' }).afterClosed().subscribe((s) => s && this.data.reload());
  }

  toggle(item: ActivityItem, completed: boolean): void {
    this.api.setActivityCompleted(item.id, completed).subscribe(() => this.data.reload());
  }

  remove(item: ActivityItem): void {
    confirm(this.dialog, { title: 'Delete activity?', message: `"${item.subject}" will be deleted.`, confirmText: 'Delete', danger: true })
      .subscribe((ok) => {
        if (!ok) return;
        this.api.activities.delete(item.id).subscribe(() => {
          this.notify.success('Activity deleted');
          this.data.reload();
        });
      });
  }
}
