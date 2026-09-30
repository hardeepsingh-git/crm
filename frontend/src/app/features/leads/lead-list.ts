import { CurrencyPipe, DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
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
import { Router, RouterLink } from '@angular/router';
import { stableValue } from '../../core/api';
import { LEAD_SOURCES, LEAD_STATUSES, LeadDetail, LeadListItem, LeadSource, LeadStatus, PagedResult } from '../../core/models';
import { CURRENCY, HumanizePipe, ListState, inputValue } from '../../shared/list';
import { LeadFormDialog } from './lead-form-dialog';

@Component({
  selector: 'app-lead-list',
  imports: [CurrencyPipe, DatePipe, RouterLink, MatTableModule, MatPaginatorModule, MatSortModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatIconModule, MatButtonModule, MatSlideToggleModule, MatProgressBarModule, HumanizePipe],
  template: `
    <div class="page-header">
      <h1>Leads</h1>
      <span class="spacer"></span>
      <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> New lead</button>
    </div>
    <div class="filters">
      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="search">
        <mat-label>Search name, company or email</mat-label>
        <input matInput [value]="state.search()" (input)="state.setSearch(inputValue($event))" />
        <mat-icon matSuffix>search</mat-icon>
      </mat-form-field>
      <mat-form-field appearance="outline" subscriptSizing="dynamic">
        <mat-label>Status</mat-label>
        <mat-select [value]="status()" (selectionChange)="status.set($event.value); state.resetPage()">
          <mat-option [value]="null">All</mat-option>
          @for (s of statuses; track s) { <mat-option [value]="s">{{ s }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field appearance="outline" subscriptSizing="dynamic">
        <mat-label>Source</mat-label>
        <mat-select [value]="source()" (selectionChange)="source.set($event.value); state.resetPage()">
          <mat-option [value]="null">All</mat-option>
          @for (s of sources; track s) { <mat-option [value]="s">{{ s | humanize }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-slide-toggle [checked]="state.mine()" (change)="state.setMine($event.checked)">My leads</mat-slide-toggle>
    </div>
    <div class="table-wrap">
      @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      <table mat-table [dataSource]="rows()" matSort (matSortChange)="state.onSort($event)">
        <ng-container matColumnDef="name">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Name</th>
          <td mat-cell *matCellDef="let l"><a [routerLink]="['/leads', l.id]">{{ l.fullName }}</a></td>
        </ng-container>
        <ng-container matColumnDef="company">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Company</th>
          <td mat-cell *matCellDef="let l">{{ l.company }}</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Status</th>
          <td mat-cell *matCellDef="let l"><span class="chip" [attr.data-v]="l.status">{{ l.status }}</span></td>
        </ng-container>
        <ng-container matColumnDef="source">
          <th mat-header-cell *matHeaderCellDef>Source</th>
          <td mat-cell *matCellDef="let l">{{ l.source | humanize }}</td>
        </ng-container>
        <ng-container matColumnDef="estimatedValue">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Est. value</th>
          <td mat-cell *matCellDef="let l">{{ l.estimatedValue | currency: currency : 'symbol' : '1.0-0' }}</td>
        </ng-container>
        <ng-container matColumnDef="owner">
          <th mat-header-cell *matHeaderCellDef>Owner</th>
          <td mat-cell *matCellDef="let l">{{ l.ownerName }}</td>
        </ng-container>
        <ng-container matColumnDef="createdAt">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Created</th>
          <td mat-cell *matCellDef="let l">{{ l.createdAt | date: 'mediumDate' }}</td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="open(row)"></tr>
        <tr class="mat-mdc-row" *matNoDataRow><td class="mat-mdc-cell empty" [attr.colspan]="columns.length">No leads found.</td></tr>
      </table>
      <mat-paginator [length]="total()" [pageIndex]="state.page()" [pageSize]="state.pageSize()" [pageSizeOptions]="[10, 20, 50, 100]"
        (page)="state.onPage($event)" />
    </div>
  `,
})
export class LeadList {
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  readonly currency = CURRENCY;
  readonly inputValue = inputValue;
  readonly statuses = LEAD_STATUSES;
  readonly sources = LEAD_SOURCES;
  readonly state = new ListState();
  readonly status = signal<LeadStatus | null>(null);
  readonly source = signal<LeadSource | null>(null);
  readonly columns = ['name', 'company', 'status', 'source', 'estimatedValue', 'owner', 'createdAt'];

  readonly data = httpResource<PagedResult<LeadListItem>>(() => ({
    url: '/api/leads',
    params: this.state.params({ status: this.status(), source: this.source() }),
  }));
  private readonly value = stableValue(this.data);
  readonly rows = computed(() => this.value()?.items ?? []);
  readonly total = computed(() => this.value()?.totalCount ?? 0);

  open(row: LeadListItem): void {
    void this.router.navigate(['/leads', row.id]);
  }

  create(): void {
    this.dialog
      .open(LeadFormDialog, { data: null, width: '720px' })
      .afterClosed()
      .subscribe((saved?: LeadDetail) => saved && this.router.navigate(['/leads', saved.id]));
  }
}
