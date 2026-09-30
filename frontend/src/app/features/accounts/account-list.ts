import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSortModule } from '@angular/material/sort';
import { MatTableModule } from '@angular/material/table';
import { Router, RouterLink } from '@angular/router';
import { stableValue } from '../../core/api';
import { AccountDetail, AccountListItem, PagedResult } from '../../core/models';
import { ListState, inputValue } from '../../shared/list';
import { AccountFormDialog } from './account-form-dialog';

@Component({
  selector: 'app-account-list',
  imports: [DatePipe, RouterLink, MatTableModule, MatPaginatorModule, MatSortModule, MatFormFieldModule, MatInputModule, MatIconModule, MatButtonModule, MatSlideToggleModule, MatProgressBarModule],
  template: `
    <div class="page-header">
      <h1>Accounts</h1>
      <span class="spacer"></span>
      <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> New account</button>
    </div>
    <div class="filters">
      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="search">
        <mat-label>Search name, email or city</mat-label>
        <input matInput [value]="state.search()" (input)="state.setSearch(inputValue($event))" />
        <mat-icon matSuffix>search</mat-icon>
      </mat-form-field>
      <mat-slide-toggle [checked]="state.mine()" (change)="state.setMine($event.checked)">My accounts</mat-slide-toggle>
    </div>
    <div class="table-wrap">
      @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      <table mat-table [dataSource]="rows()" matSort (matSortChange)="state.onSort($event)">
        <ng-container matColumnDef="name">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Name</th>
          <td mat-cell *matCellDef="let a"><a [routerLink]="['/accounts', a.id]">{{ a.name }}</a></td>
        </ng-container>
        <ng-container matColumnDef="industry">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Industry</th>
          <td mat-cell *matCellDef="let a">{{ a.industry }}</td>
        </ng-container>
        <ng-container matColumnDef="city">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Location</th>
          <td mat-cell *matCellDef="let a">{{ a.city }}{{ a.city && a.country ? ', ' : '' }}{{ a.country }}</td>
        </ng-container>
        <ng-container matColumnDef="phone">
          <th mat-header-cell *matHeaderCellDef>Phone</th>
          <td mat-cell *matCellDef="let a">{{ a.phone }}</td>
        </ng-container>
        <ng-container matColumnDef="counts">
          <th mat-header-cell *matHeaderCellDef>Contacts / Open deals</th>
          <td mat-cell *matCellDef="let a">{{ a.contactCount }} / {{ a.openOpportunityCount }}</td>
        </ng-container>
        <ng-container matColumnDef="owner">
          <th mat-header-cell *matHeaderCellDef>Owner</th>
          <td mat-cell *matCellDef="let a">{{ a.ownerName }}</td>
        </ng-container>
        <ng-container matColumnDef="createdAt">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Created</th>
          <td mat-cell *matCellDef="let a">{{ a.createdAt | date: 'mediumDate' }}</td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="open(row)"></tr>
        <tr class="mat-mdc-row" *matNoDataRow><td class="mat-mdc-cell empty" [attr.colspan]="columns.length">No accounts found.</td></tr>
      </table>
      <mat-paginator [length]="total()" [pageIndex]="state.page()" [pageSize]="state.pageSize()" [pageSizeOptions]="[10, 20, 50, 100]"
        (page)="state.onPage($event)" />
    </div>
  `,
})
export class AccountList {
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  readonly inputValue = inputValue;
  readonly state = new ListState();
  readonly columns = ['name', 'industry', 'city', 'phone', 'counts', 'owner', 'createdAt'];

  readonly data = httpResource<PagedResult<AccountListItem>>(() => ({ url: '/api/accounts', params: this.state.params() }));
  private readonly value = stableValue(this.data);
  readonly rows = computed(() => this.value()?.items ?? []);
  readonly total = computed(() => this.value()?.totalCount ?? 0);

  open(row: AccountListItem): void {
    void this.router.navigate(['/accounts', row.id]);
  }

  create(): void {
    this.dialog
      .open(AccountFormDialog, { data: null, width: '720px' })
      .afterClosed()
      .subscribe((saved?: AccountDetail) => saved && this.router.navigate(['/accounts', saved.id]));
  }
}
