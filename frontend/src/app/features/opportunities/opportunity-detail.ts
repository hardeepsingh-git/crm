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
import { OpportunityDetail, StageDef } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { ActivityPanel } from '../../shared/activity-panel';
import { AttachmentsPanel } from '../../shared/attachments-panel';
import { confirm } from '../../shared/confirm-dialog';
import { CustomFieldsView } from '../../shared/custom-fields';
import { RecordHistory, StageChip, StagePath } from '../../shared/record-widgets';
import { OpportunityFormDialog } from './opportunity-form-dialog';

@Component({
  selector: 'app-opportunity-detail',
  imports: [CurrencyPipe, DatePipe, RouterLink, MatButtonModule, MatCardModule, MatIconModule, MatProgressBarModule, ActivityPanel, AttachmentsPanel, CustomFieldsView, RecordHistory, StageChip, StagePath],
  template: `
    @if (opportunity.isLoading() && !o()) { <mat-progress-bar mode="indeterminate" /> }
    @if (o(); as o) {
      <div class="page-header">
        <div>
          <a routerLink="/opportunities" class="back"><mat-icon inline>arrow_back</mat-icon> {{ meta.label('opportunity', true) }}</a>
          <h1>{{ o.name }}</h1>
          <div class="muted"><a [routerLink]="['/accounts', o.accountId]">{{ o.accountName }}</a> &middot; Owner: {{ o.ownerName }}</div>
        </div>
        <span class="spacer"></span>
        @if (auth.canModify('opportunity', o.ownerId)) {
          <button mat-stroked-button (click)="edit(o)"><mat-icon>edit</mat-icon> Edit</button>
        }
        @if (auth.canDelete('opportunity', o.ownerId)) {
          <button mat-stroked-button class="danger" (click)="remove(o)"><mat-icon>delete</mat-icon> Delete</button>
        }
      </div>

      <app-stage-path objectKey="opportunity" [stage]="o.stage" [disabled]="!auth.canModify('opportunity', o.ownerId)" (selected)="onStage(o, $event)" />

      <div class="detail-layout">
        <mat-card appearance="outlined" class="info">
          <mat-card-content>
            <dl class="fields">
              <dt>Stage</dt><dd><app-stage-chip objectKey="opportunity" [stage]="o.stage" /></dd>
              <dt>Amount</dt><dd>{{ o.amount | currency: meta.currency() : 'symbol' : '1.0-0' }}</dd>
              <dt>Probability</dt><dd>{{ o.probability }}%</dd>
              <dt>Weighted</dt><dd>{{ (o.amount * o.probability) / 100 | currency: meta.currency() : 'symbol' : '1.0-0' }}</dd>
              <dt>Expected close</dt><dd>{{ o.expectedCloseDate | date: 'mediumDate' }}</dd>
              @if (o.closedAt) { <dt>Closed</dt><dd>{{ o.closedAt | date: 'medium' }}</dd> }
              @if (o.lossReason) { <dt>Loss reason</dt><dd>{{ o.lossReason }}</dd> }
              <dt>Contact</dt><dd>@if (o.contactId) { <a [routerLink]="['/contacts', o.contactId]">{{ o.contactName }}</a> }</dd>
              <app-custom-fields-view [fields]="meta.fields('opportunity')" [values]="o.customFields" />
              <dt>Created</dt><dd>{{ o.createdAt | date: 'medium' }}</dd>
              <dt>Updated</dt><dd>{{ o.updatedAt | date: 'medium' }}</dd>
            </dl>
            @if (o.description) { <p class="desc">{{ o.description }}</p> }
            <app-attachments-panel objectKey="opportunity" [recordId]="o.id" [ownerId]="o.ownerId" (changed)="version.set(version() + 1)" />
            <app-record-history entityType="opportunity" [entityId]="o.id" [version]="version()" />
          </mat-card-content>
        </mat-card>
        @if (auth.can('activity', 'read')) {
        <mat-card appearance="outlined"><mat-card-content>
          <app-activity-panel filterBy="opportunityId" [links]="{ opportunityId: o.id, accountId: o.accountId, contactId: o.contactId }" />
        </mat-card-content></mat-card>
        }
      </div>
    }
  `,
})
export class OpportunityDetailPage {
  readonly id = input.required<string>();
  readonly auth = inject(AuthService);
  readonly meta = inject(MetadataService);
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  readonly version = signal(0);

  readonly opportunity = httpResource<OpportunityDetail>(() => `/api/opportunities/${this.id()}`);
  readonly o = computed(() => (this.opportunity.hasValue() ? this.opportunity.value() : undefined));

  onStage(o: OpportunityDetail, stage: StageDef): void {
    if (stage.category !== 'Lost') {
      this.setStage(o, stage, null);
      return;
    }
    confirm(this.dialog, { title: `Mark as ${stage.label}?`, message: 'Optionally record why this was lost.', confirmText: stage.label, input: { label: 'Loss reason' } })
      .subscribe((reason) => reason !== undefined && this.setStage(o, stage, typeof reason === 'string' ? reason : null));
  }

  private setStage(o: OpportunityDetail, stage: StageDef, lossReason: string | null): void {
    this.api.changeStage(o.id, stage.key, lossReason).subscribe((updated) => {
      this.opportunity.set(updated);
      this.version.update((v) => v + 1);
      if (stage.category === 'Won') this.notify.success(`"${o.name}" won!`);
    });
  }

  edit(o: OpportunityDetail): void {
    this.dialog.open(OpportunityFormDialog, { data: { item: o }, width: '720px' }).afterClosed().subscribe((saved) => {
      if (!saved) return;
      this.opportunity.reload();
      this.version.update((v) => v + 1);
    });
  }

  remove(o: OpportunityDetail): void {
    confirm(this.dialog, {
      title: 'Delete opportunity?',
      message: `"${o.name}" and its activities will be deleted. This cannot be undone.`,
      confirmText: 'Delete',
      danger: true,
    }).subscribe((ok) => {
      if (!ok) return;
      this.api.opportunities.delete(o.id).subscribe(() => {
        this.notify.success('Opportunity deleted');
        void this.router.navigateByUrl('/opportunities');
      });
    });
  }
}
