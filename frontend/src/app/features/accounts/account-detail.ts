import { CurrencyPipe, DatePipe, DecimalPipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { Router, RouterLink } from '@angular/router';
import { CrmApi, toParams } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { AccountDetail, ContactListItem, OpportunityListItem, PagedResult } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { ActivityPanel } from '../../shared/activity-panel';
import { confirm } from '../../shared/confirm-dialog';
import { CURRENCY, HumanizePipe } from '../../shared/list';
import { ContactFormDialog } from '../contacts/contact-form-dialog';
import { OpportunityFormDialog } from '../opportunities/opportunity-form-dialog';
import { AccountFormDialog } from './account-form-dialog';

@Component({
  selector: 'app-account-detail',
  imports: [CurrencyPipe, DatePipe, DecimalPipe, RouterLink, MatButtonModule, MatCardModule, MatIconModule, MatProgressBarModule, MatTableModule, MatTabsModule, ActivityPanel, HumanizePipe],
  template: `
    @if (account.isLoading() && !a()) { <mat-progress-bar mode="indeterminate" /> }
    @if (a(); as a) {
      <div class="page-header">
        <div>
          <a routerLink="/accounts" class="back"><mat-icon inline>arrow_back</mat-icon> Accounts</a>
          <h1>{{ a.name }}</h1>
          <div class="muted">{{ a.industry }} &middot; Owner: {{ a.ownerName }}</div>
        </div>
        <span class="spacer"></span>
        @if (auth.canModify(a.ownerId)) {
          <button mat-stroked-button (click)="edit(a)"><mat-icon>edit</mat-icon> Edit</button>
        }
        @if (auth.isManager()) {
          <button mat-stroked-button class="danger" (click)="remove(a)"><mat-icon>delete</mat-icon> Delete</button>
        }
      </div>

      <div class="detail-layout">
        <mat-card appearance="outlined" class="info">
          <mat-card-content>
            <dl class="fields">
              <dt>Website</dt><dd>@if (a.website) { <a [href]="a.website" target="_blank" rel="noopener noreferrer">{{ a.website }}</a> }</dd>
              <dt>Phone</dt><dd>{{ a.phone }}</dd>
              <dt>Email</dt><dd>@if (a.email) { <a [href]="'mailto:' + a.email">{{ a.email }}</a> }</dd>
              <dt>Address</dt><dd>{{ address(a) }}</dd>
              <dt>Annual revenue</dt><dd>{{ a.annualRevenue | currency: currency : 'symbol' : '1.0-0' }}</dd>
              <dt>Employees</dt><dd>{{ a.employeeCount | number }}</dd>
              <dt>Open pipeline</dt><dd>{{ a.openPipelineAmount | currency: currency : 'symbol' : '1.0-0' }} ({{ a.openOpportunityCount }} deals)</dd>
              <dt>Created</dt><dd>{{ a.createdAt | date: 'medium' }}</dd>
              <dt>Updated</dt><dd>{{ a.updatedAt | date: 'medium' }}</dd>
            </dl>
            @if (a.description) { <p class="desc">{{ a.description }}</p> }
          </mat-card-content>
        </mat-card>

        <mat-card appearance="outlined">
          <mat-tab-group animationDuration="0">
            <mat-tab [label]="'Contacts (' + a.contactCount + ')'">
              <div class="tab-actions"><button mat-stroked-button (click)="addContact(a)"><mat-icon>person_add</mat-icon> Add contact</button></div>
              <table mat-table [dataSource]="contactRows()">
                <ng-container matColumnDef="name"><th mat-header-cell *matHeaderCellDef>Name</th>
                  <td mat-cell *matCellDef="let c"><a [routerLink]="['/contacts', c.id]">{{ c.fullName }}</a></td></ng-container>
                <ng-container matColumnDef="title"><th mat-header-cell *matHeaderCellDef>Title</th><td mat-cell *matCellDef="let c">{{ c.jobTitle }}</td></ng-container>
                <ng-container matColumnDef="email"><th mat-header-cell *matHeaderCellDef>Email</th><td mat-cell *matCellDef="let c">{{ c.email }}</td></ng-container>
                <ng-container matColumnDef="phone"><th mat-header-cell *matHeaderCellDef>Phone</th><td mat-cell *matCellDef="let c">{{ c.phone }}</td></ng-container>
                <tr mat-header-row *matHeaderRowDef="contactColumns"></tr>
                <tr mat-row *matRowDef="let row; columns: contactColumns"></tr>
                <tr class="mat-mdc-row" *matNoDataRow><td class="mat-mdc-cell empty" colspan="4">No contacts.</td></tr>
              </table>
            </mat-tab>
            <mat-tab label="Opportunities">
              <div class="tab-actions"><button mat-stroked-button (click)="addOpportunity(a)"><mat-icon>add</mat-icon> Add opportunity</button></div>
              <table mat-table [dataSource]="oppRows()">
                <ng-container matColumnDef="name"><th mat-header-cell *matHeaderCellDef>Name</th>
                  <td mat-cell *matCellDef="let o"><a [routerLink]="['/opportunities', o.id]">{{ o.name }}</a></td></ng-container>
                <ng-container matColumnDef="stage"><th mat-header-cell *matHeaderCellDef>Stage</th>
                  <td mat-cell *matCellDef="let o"><span class="chip" [attr.data-v]="o.stage">{{ o.stage | humanize }}</span></td></ng-container>
                <ng-container matColumnDef="amount"><th mat-header-cell *matHeaderCellDef>Amount</th>
                  <td mat-cell *matCellDef="let o">{{ o.amount | currency: currency : 'symbol' : '1.0-0' }}</td></ng-container>
                <ng-container matColumnDef="close"><th mat-header-cell *matHeaderCellDef>Close date</th>
                  <td mat-cell *matCellDef="let o">{{ o.expectedCloseDate | date: 'mediumDate' }}</td></ng-container>
                <tr mat-header-row *matHeaderRowDef="oppColumns"></tr>
                <tr mat-row *matRowDef="let row; columns: oppColumns"></tr>
                <tr class="mat-mdc-row" *matNoDataRow><td class="mat-mdc-cell empty" colspan="4">No opportunities.</td></tr>
              </table>
            </mat-tab>
            <mat-tab label="Activities">
              <div class="tab-body"><app-activity-panel [links]="{ accountId: a.id }" /></div>
            </mat-tab>
          </mat-tab-group>
        </mat-card>
      </div>
    }
  `,
})
export class AccountDetailPage {
  readonly id = input.required<string>();
  readonly auth = inject(AuthService);
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  readonly currency = CURRENCY;
  readonly contactColumns = ['name', 'title', 'email', 'phone'];
  readonly oppColumns = ['name', 'stage', 'amount', 'close'];

  readonly account = httpResource<AccountDetail>(() => `/api/accounts/${this.id()}`);
  readonly a = computed(() => (this.account.hasValue() ? this.account.value() : undefined));
  readonly contacts = httpResource<PagedResult<ContactListItem>>(() => ({
    url: '/api/contacts',
    params: toParams({ accountId: this.id(), pageSize: 100 }),
  }));
  readonly opportunities = httpResource<PagedResult<OpportunityListItem>>(() => ({
    url: '/api/opportunities',
    params: toParams({ accountId: this.id(), pageSize: 100, sortBy: 'createdAt', sortDir: 'desc' }),
  }));
  readonly contactRows = computed(() => (this.contacts.hasValue() ? this.contacts.value().items : []));
  readonly oppRows = computed(() => (this.opportunities.hasValue() ? this.opportunities.value().items : []));

  address(a: AccountDetail): string {
    return [a.street, a.city, a.state, a.postalCode, a.country].filter((x) => !!x).join(', ');
  }

  edit(a: AccountDetail): void {
    this.dialog.open(AccountFormDialog, { data: a, width: '720px' }).afterClosed()
      .subscribe((saved) => saved && this.account.reload());
  }

  addContact(a: AccountDetail): void {
    this.dialog.open(ContactFormDialog, { data: { accountId: a.id }, width: '720px' }).afterClosed().subscribe((saved) => {
      if (!saved) return;
      this.contacts.reload();
      this.account.reload();
    });
  }

  addOpportunity(a: AccountDetail): void {
    this.dialog.open(OpportunityFormDialog, { data: { accountId: a.id }, width: '720px' }).afterClosed().subscribe((saved) => {
      if (!saved) return;
      this.opportunities.reload();
      this.account.reload();
    });
  }

  remove(a: AccountDetail): void {
    confirm(this.dialog, {
      title: 'Delete account?',
      message: `"${a.name}" will be deleted. Contacts will be unlinked and its activities removed. This cannot be undone.`,
      confirmText: 'Delete',
      danger: true,
    }).subscribe((ok) => {
      if (!ok) return;
      this.api.accounts.delete(a.id).subscribe(() => {
        this.notify.success('Account deleted');
        void this.router.navigateByUrl('/accounts');
      });
    });
  }
}
