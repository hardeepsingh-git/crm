import { CurrencyPipe, DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
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
import { OpportunityDetail, OpportunityListItem, OpportunityStage, PagedResult, STAGES } from '../../core/models';
import { CURRENCY, HumanizePipe, ListState, inputValue } from '../../shared/list';
import { OpportunityFormDialog } from './opportunity-form-dialog';

@Component({
  selector: 'app-opportunity-list',
  imports: [CurrencyPipe, DatePipe, RouterLink, MatTableModule, MatPaginatorModule, MatSortModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatIconModule, MatButtonModule, MatButtonToggleModule, MatSlideToggleModule, MatProgressBarModule, HumanizePipe],
  template: `
    <div class="page-header">
      <h1>Opportunities</h1>
      <span class="spacer"></span>
      <a mat-stroked-button routerLink="/pipeline"><mat-icon>view_kanban</mat-icon> Pipeline board</a>
      <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> New opportunity</button>
    </div>
    <div class="filters">
      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="search">
        <mat-label>Search name or account</mat-label>
        <input matInput [value]="state.search()" (input)="state.setSearch(inputValue($event))" />
        <mat-icon matSuffix>search</mat-icon>
      </mat-form-field>
      <mat-button-toggle-group [value]="open()" (change)="open.set($event.value); state.resetPage()" hideSingleSelectionIndicator>
        <mat-button-toggle [value]="true">Open</mat-button-toggle>
        <mat-button-toggle [value]="null">All</mat-button-toggle>
      </mat-button-toggle-group>
      <mat-form-field appearance="outline" subscriptSizing="dynamic">
        <mat-label>Stage</mat-label>
        <mat-select [value]="stage()" (selectionChange)="stage.set($event.value); state.resetPage()">
          <mat-option [value]="null">All</mat-option>
          @for (s of stages; track s) { <mat-option [value]="s">{{ s | humanize }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-slide-toggle [checked]="state.mine()" (change)="state.setMine($event.checked)">My deals</mat-slide-toggle>
    </div>
    <div class="table-wrap">
      @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      <table mat-table [dataSource]="rows()" matSort (matSortChange)="state.onSort($event)">
        <ng-container matColumnDef="name">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Name</th>
          <td mat-cell *matCellDef="let o"><a [routerLink]="['/opportunities', o.id]">{{ o.name }}</a></td>
        </ng-container>
        <ng-container matColumnDef="account">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Account</th>
          <td mat-cell *matCellDef="let o"><a [routerLink]="['/accounts', o.accountId]" (click)="$event.stopPropagation()">{{ o.accountName }}</a></td>
        </ng-container>
        <ng-container matColumnDef="stage">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Stage</th>
          <td mat-cell *matCellDef="let o"><span class="chip" [attr.data-v]="o.stage">{{ o.stage | humanize }}</span></td>
        </ng-container>
        <ng-container matColumnDef="amount">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Amount</th>
          <td mat-cell *matCellDef="let o">{{ o.amount | currency: currency : 'symbol' : '1.0-0' }}</td>
        </ng-container>
        <ng-container matColumnDef="probability">
          <th mat-header-cell *matHeaderCellDef>Prob.</th>
          <td mat-cell *matCellDef="let o">{{ o.probability }}%</td>
        </ng-container>
        <ng-container matColumnDef="expectedCloseDate">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Close date</th>
          <td mat-cell *matCellDef="let o">{{ o.expectedCloseDate | date: 'mediumDate' }}</td>
        </ng-container>
        <ng-container matColumnDef="owner">
          <th mat-header-cell *matHeaderCellDef>Owner</th>
          <td mat-cell *matCellDef="let o">{{ o.ownerName }}</td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="openRow(row)"></tr>
        <tr class="mat-mdc-row" *matNoDataRow><td class="mat-mdc-cell empty" [attr.colspan]="columns.length">No opportunities found.</td></tr>
      </table>
      <mat-paginator [length]="total()" [pageIndex]="state.page()" [pageSize]="state.pageSize()" [pageSizeOptions]="[10, 20, 50, 100]"
        (page)="state.onPage($event)" />
    </div>
  `,
})
export class OpportunityList {
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  readonly currency = CURRENCY;
  readonly inputValue = inputValue;
  readonly stages = STAGES;
  readonly state = new ListState();
  readonly stage = signal<OpportunityStage | null>(null);
  readonly open = signal<boolean | null>(true);
  readonly columns = ['name', 'account', 'stage', 'amount', 'probability', 'expectedCloseDate', 'owner'];

  readonly data = httpResource<PagedResult<OpportunityListItem>>(() => ({
    url: '/api/opportunities',
    params: this.state.params({ stage: this.stage(), open: this.open() }),
  }));
  private readonly value = stableValue(this.data);
  readonly rows = computed(() => this.value()?.items ?? []);
  readonly total = computed(() => this.value()?.totalCount ?? 0);

  openRow(row: OpportunityListItem): void {
    void this.router.navigate(['/opportunities', row.id]);
  }

  create(): void {
    this.dialog
      .open(OpportunityFormDialog, { data: null, width: '720px' })
      .afterClosed()
      .subscribe((saved?: OpportunityDetail) => saved && this.router.navigate(['/opportunities', saved.id]));
  }
}
