import { DatePipe, LowerCasePipe } from '@angular/common';
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
import { AuthService } from '../../core/auth.service';
import { MetadataService } from '../../core/metadata.service';
import { ContactDetail, ContactListItem, PagedResult } from '../../core/models';
import { CfFilters, CfValue } from '../../shared/custom-fields';
import { ListState, inputValue } from '../../shared/list';
import { ExportButton } from '../../shared/export-button';
import { ContactFormDialog } from './contact-form-dialog';

@Component({
  selector: 'app-contact-list',
  imports: [DatePipe, LowerCasePipe, RouterLink, MatTableModule, MatPaginatorModule, MatSortModule, MatFormFieldModule, MatInputModule, MatIconModule, MatButtonModule, MatSlideToggleModule, MatProgressBarModule, CfValue, CfFilters, ExportButton],
  template: `
    <div class="page-header">
      <h1>{{ meta.label('contact', true) }}</h1>
      <span class="spacer"></span>
      <app-export-button objectKey="contact" [params]="state.params()" />
      @if (auth.can('contact', 'create')) {
        <button mat-flat-button (click)="create()"><mat-icon>person_add</mat-icon> New {{ meta.label('contact') | lowercase }}</button>
      }
    </div>
    <div class="filters">
      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="search">
        <mat-label>Search name, email or account</mat-label>
        <input matInput [value]="state.search()" (input)="state.setSearch(inputValue($event))" />
        <mat-icon matSuffix>search</mat-icon>
      </mat-form-field>
      <app-cf-filters objectKey="contact" [state]="state" />
      <mat-slide-toggle [checked]="state.mine()" (change)="state.setMine($event.checked)">Mine only</mat-slide-toggle>
    </div>
    <div class="table-wrap">
      @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      <table mat-table [dataSource]="rows()" matSort (matSortChange)="state.onSort($event)">
        <ng-container matColumnDef="lastName">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Name</th>
          <td mat-cell *matCellDef="let c"><a [routerLink]="['/contacts', c.id]">{{ c.fullName }}</a></td>
        </ng-container>
        <ng-container matColumnDef="jobTitle">
          <th mat-header-cell *matHeaderCellDef>Title</th>
          <td mat-cell *matCellDef="let c">{{ c.jobTitle }}</td>
        </ng-container>
        <ng-container matColumnDef="account">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Account</th>
          <td mat-cell *matCellDef="let c">
            @if (c.accountId) { <a [routerLink]="['/accounts', c.accountId]" (click)="$event.stopPropagation()">{{ c.accountName }}</a> }
          </td>
        </ng-container>
        <ng-container matColumnDef="email">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Email</th>
          <td mat-cell *matCellDef="let c">{{ c.email }}</td>
        </ng-container>
        <ng-container matColumnDef="phone">
          <th mat-header-cell *matHeaderCellDef>Phone</th>
          <td mat-cell *matCellDef="let c">{{ c.phone }}</td>
        </ng-container>
        <ng-container matColumnDef="owner">
          <th mat-header-cell *matHeaderCellDef>Owner</th>
          <td mat-cell *matCellDef="let c">{{ c.ownerName }}</td>
        </ng-container>
        @for (f of cfFields(); track f.key) {
          <ng-container [matColumnDef]="'cf.' + f.key">
            <th mat-header-cell *matHeaderCellDef>{{ f.label }}</th>
            <td mat-cell *matCellDef="let c"><app-cf-value [field]="f" [value]="c.customFields[f.key]" /></td>
          </ng-container>
        }
        <ng-container matColumnDef="createdAt">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Created</th>
          <td mat-cell *matCellDef="let c">{{ c.createdAt | date: 'mediumDate' }}</td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns()"></tr>
        <tr mat-row *matRowDef="let row; columns: columns()" class="clickable" (click)="open(row)"></tr>
        <tr class="mat-mdc-row" *matNoDataRow><td class="mat-mdc-cell empty" [attr.colspan]="columns().length">No records found.</td></tr>
      </table>
      <mat-paginator [length]="total()" [pageIndex]="state.page()" [pageSize]="state.pageSize()" [pageSizeOptions]="[10, 20, 50, 100]"
        (page)="state.onPage($event)" />
    </div>
  `,
})
export class ContactList {
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  readonly auth = inject(AuthService);
  readonly meta = inject(MetadataService);
  readonly inputValue = inputValue;
  readonly state = new ListState();
  readonly cfFields = computed(() => this.meta.listFields('contact'));
  readonly columns = computed(() => [
    'lastName', 'jobTitle', 'account', 'email', 'phone', ...this.cfFields().map((f) => `cf.${f.key}`), 'owner', 'createdAt',
  ]);

  readonly data = httpResource<PagedResult<ContactListItem>>(() => ({ url: '/api/contacts', params: this.state.params() }));
  private readonly value = stableValue(this.data);
  readonly rows = computed(() => this.value()?.items ?? []);
  readonly total = computed(() => this.value()?.totalCount ?? 0);

  open(row: ContactListItem): void {
    void this.router.navigate(['/contacts', row.id]);
  }

  create(): void {
    this.dialog
      .open(ContactFormDialog, { data: null, width: '720px' })
      .afterClosed()
      .subscribe((saved?: ContactDetail) => saved && this.router.navigate(['/contacts', saved.id]));
  }
}
