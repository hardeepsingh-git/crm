import { CurrencyPipe, DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { CrmApi } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { MetadataService } from '../../core/metadata.service';
import { LeadConvertResult, LeadDetail } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { ActivityPanel } from '../../shared/activity-panel';
import { confirm } from '../../shared/confirm-dialog';
import { CustomFieldsView } from '../../shared/custom-fields';
import { HumanizePipe } from '../../shared/list';
import { RecordHistory } from '../../shared/record-widgets';
import { LeadConvertDialog } from './lead-convert-dialog';
import { LeadFormDialog } from './lead-form-dialog';

@Component({
  selector: 'app-lead-detail',
  imports: [CurrencyPipe, DatePipe, RouterLink, MatButtonModule, MatCardModule, MatIconModule, MatProgressBarModule, ActivityPanel, HumanizePipe, CustomFieldsView, RecordHistory],
  template: `
    @if (lead.isLoading() && !l()) { <mat-progress-bar mode="indeterminate" /> }
    @if (l(); as l) {
      <div class="page-header">
        <div>
          <a routerLink="/leads" class="back"><mat-icon inline>arrow_back</mat-icon> {{ meta.label('lead', true) }}</a>
          <h1>{{ l.fullName }} <span class="chip" [attr.data-v]="l.status">{{ l.status }}</span></h1>
          <div class="muted">{{ l.jobTitle }} &middot; {{ l.company }}</div>
        </div>
        <span class="spacer"></span>
        @if (l.status !== 'Converted' && auth.canModify('lead', l.ownerId)) {
          @if (canConvert()) {
            <button mat-flat-button (click)="convert(l)"><mat-icon>swap_horiz</mat-icon> Convert</button>
          }
          <button mat-stroked-button (click)="edit(l)"><mat-icon>edit</mat-icon> Edit</button>
        }
        @if (auth.canDelete('lead', l.ownerId)) {
          <button mat-stroked-button class="danger" (click)="remove(l)"><mat-icon>delete</mat-icon> Delete</button>
        }
      </div>
      @if (l.status === 'Converted') {
        <div class="banner">
          <mat-icon>check_circle</mat-icon>
          Converted {{ l.convertedAt | date: 'medium' }}:
          @if (l.convertedAccountId) { <a [routerLink]="['/accounts', l.convertedAccountId]">Account</a> }
          @if (l.convertedContactId) { &middot; <a [routerLink]="['/contacts', l.convertedContactId]">Contact</a> }
          @if (l.convertedOpportunityId) { &middot; <a [routerLink]="['/opportunities', l.convertedOpportunityId]">Opportunity</a> }
        </div>
      }
      <div class="detail-layout">
        <mat-card appearance="outlined" class="info">
          <mat-card-content>
            <dl class="fields">
              <dt>Email</dt><dd>@if (l.email) { <a [href]="'mailto:' + l.email">{{ l.email }}</a> }</dd>
              <dt>Phone</dt><dd>{{ l.phone }}</dd>
              <dt>Source</dt><dd>{{ l.source | humanize }}</dd>
              <dt>Estimated value</dt><dd>{{ l.estimatedValue | currency: meta.currency() : 'symbol' : '1.0-0' }}</dd>
              <dt>Owner</dt><dd>{{ l.ownerName }}</dd>
              <app-custom-fields-view [fields]="meta.fields('lead')" [values]="l.customFields" />
              <dt>Created</dt><dd>{{ l.createdAt | date: 'medium' }}</dd>
              <dt>Updated</dt><dd>{{ l.updatedAt | date: 'medium' }}</dd>
            </dl>
            @if (l.description) { <p class="desc">{{ l.description }}</p> }
            <app-record-history entityType="lead" [entityId]="l.id" [version]="version()" />
          </mat-card-content>
        </mat-card>
        @if (auth.can('activity', 'read')) {
        <mat-card appearance="outlined"><mat-card-content>
          <app-activity-panel filterBy="leadId" [links]="{ leadId: l.id }" />
        </mat-card-content></mat-card>
        }
      </div>
    }
  `,
})
export class LeadDetailPage {
  readonly id = input.required<string>();
  readonly auth = inject(AuthService);
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  readonly meta = inject(MetadataService);
  readonly version = signal(0);
  /** Conversion creates an account and contact (and optionally an opportunity). */
  readonly canConvert = computed(() => this.auth.can('account', 'create') && this.auth.can('contact', 'create'));

  readonly lead = httpResource<LeadDetail>(() => `/api/leads/${this.id()}`);
  readonly l = computed(() => (this.lead.hasValue() ? this.lead.value() : undefined));

  edit(l: LeadDetail): void {
    this.dialog.open(LeadFormDialog, { data: l, width: '720px' }).afterClosed().subscribe((saved) => {
      if (!saved) return;
      this.lead.reload();
      this.version.update((v) => v + 1);
    });
  }

  convert(l: LeadDetail): void {
    this.dialog.open(LeadConvertDialog, { data: l, width: '560px' }).afterClosed().subscribe((result?: LeadConvertResult) => {
      if (!result) return;
      void this.router.navigate(result.opportunityId ? ['/opportunities', result.opportunityId] : ['/accounts', result.accountId]);
    });
  }

  remove(l: LeadDetail): void {
    confirm(this.dialog, {
      title: 'Delete lead?',
      message: `"${l.fullName}" and their activities will be deleted. This cannot be undone.`,
      confirmText: 'Delete',
      danger: true,
    }).subscribe((ok) => {
      if (!ok) return;
      this.api.leads.delete(l.id).subscribe(() => {
        this.notify.success('Lead deleted');
        void this.router.navigateByUrl('/leads');
      });
    });
  }
}
