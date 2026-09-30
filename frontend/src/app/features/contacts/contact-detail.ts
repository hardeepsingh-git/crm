import { CurrencyPipe, DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { Router, RouterLink } from '@angular/router';
import { CrmApi, toParams } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { ContactDetail, OpportunityListItem, PagedResult } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { ActivityPanel } from '../../shared/activity-panel';
import { confirm } from '../../shared/confirm-dialog';
import { CURRENCY, HumanizePipe } from '../../shared/list';
import { ContactFormDialog } from './contact-form-dialog';

@Component({
  selector: 'app-contact-detail',
  imports: [CurrencyPipe, DatePipe, RouterLink, MatButtonModule, MatCardModule, MatIconModule, MatProgressBarModule, MatTableModule, ActivityPanel, HumanizePipe],
  template: `
    @if (contact.isLoading() && !c()) { <mat-progress-bar mode="indeterminate" /> }
    @if (c(); as c) {
      <div class="page-header">
        <div>
          <a routerLink="/contacts" class="back"><mat-icon inline>arrow_back</mat-icon> Contacts</a>
          <h1>{{ c.fullName }}</h1>
          <div class="muted">
            {{ c.jobTitle }}
            @if (c.accountId) { at <a [routerLink]="['/accounts', c.accountId]">{{ c.accountName }}</a> }
          </div>
        </div>
        <span class="spacer"></span>
        @if (auth.canModify(c.ownerId)) {
          <button mat-stroked-button (click)="edit(c)"><mat-icon>edit</mat-icon> Edit</button>
        }
        @if (auth.isManager()) {
          <button mat-stroked-button class="danger" (click)="remove(c)"><mat-icon>delete</mat-icon> Delete</button>
        }
      </div>
      <div class="detail-layout">
        <mat-card appearance="outlined" class="info">
          <mat-card-content>
            <dl class="fields">
              <dt>Email</dt><dd>@if (c.email) { <a [href]="'mailto:' + c.email">{{ c.email }}</a> }</dd>
              <dt>Phone</dt><dd>{{ c.phone }}</dd>
              <dt>Mobile</dt><dd>{{ c.mobile }}</dd>
              <dt>Department</dt><dd>{{ c.department }}</dd>
              <dt>Owner</dt><dd>{{ c.ownerName }}</dd>
              <dt>Created</dt><dd>{{ c.createdAt | date: 'medium' }}</dd>
              <dt>Updated</dt><dd>{{ c.updatedAt | date: 'medium' }}</dd>
            </dl>
            @if (c.description) { <p class="desc">{{ c.description }}</p> }
          </mat-card-content>
        </mat-card>
        <div class="stack">
          <mat-card appearance="outlined">
            <mat-card-header><mat-card-title>Opportunities</mat-card-title></mat-card-header>
            <table mat-table [dataSource]="oppRows()">
              <ng-container matColumnDef="name"><th mat-header-cell *matHeaderCellDef>Name</th>
                <td mat-cell *matCellDef="let o"><a [routerLink]="['/opportunities', o.id]">{{ o.name }}</a></td></ng-container>
              <ng-container matColumnDef="stage"><th mat-header-cell *matHeaderCellDef>Stage</th>
                <td mat-cell *matCellDef="let o"><span class="chip" [attr.data-v]="o.stage">{{ o.stage | humanize }}</span></td></ng-container>
              <ng-container matColumnDef="amount"><th mat-header-cell *matHeaderCellDef>Amount</th>
                <td mat-cell *matCellDef="let o">{{ o.amount | currency: currency : 'symbol' : '1.0-0' }}</td></ng-container>
              <tr mat-header-row *matHeaderRowDef="oppColumns"></tr>
              <tr mat-row *matRowDef="let row; columns: oppColumns"></tr>
              <tr class="mat-mdc-row" *matNoDataRow><td class="mat-mdc-cell empty" colspan="3">No opportunities.</td></tr>
            </table>
          </mat-card>
          <mat-card appearance="outlined"><mat-card-content>
            <app-activity-panel filterBy="contactId" [links]="{ contactId: c.id, accountId: c.accountId }" />
          </mat-card-content></mat-card>
        </div>
      </div>
    }
  `,
})
export class ContactDetailPage {
  readonly id = input.required<string>();
  readonly auth = inject(AuthService);
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  readonly currency = CURRENCY;
  readonly oppColumns = ['name', 'stage', 'amount'];

  readonly contact = httpResource<ContactDetail>(() => `/api/contacts/${this.id()}`);
  readonly c = computed(() => (this.contact.hasValue() ? this.contact.value() : undefined));
  private readonly opportunities = httpResource<PagedResult<OpportunityListItem>>(() => ({
    url: '/api/opportunities',
    params: toParams({ contactId: this.id(), pageSize: 100 }),
  }));
  readonly oppRows = computed(() => (this.opportunities.hasValue() ? this.opportunities.value().items : []));

  edit(c: ContactDetail): void {
    this.dialog.open(ContactFormDialog, { data: { item: c }, width: '720px' }).afterClosed()
      .subscribe((saved) => saved && this.contact.reload());
  }

  remove(c: ContactDetail): void {
    confirm(this.dialog, {
      title: 'Delete contact?',
      message: `"${c.fullName}" and their activities will be deleted. This cannot be undone.`,
      confirmText: 'Delete',
      danger: true,
    }).subscribe((ok) => {
      if (!ok) return;
      this.api.contacts.delete(c.id).subscribe(() => {
        this.notify.success('Contact deleted');
        void this.router.navigateByUrl('/contacts');
      });
    });
  }
}
