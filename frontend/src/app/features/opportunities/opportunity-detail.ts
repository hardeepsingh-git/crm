import { CurrencyPipe, DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { CrmApi } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { OPEN_STAGES, OpportunityDetail, OpportunityStage } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { ActivityPanel } from '../../shared/activity-panel';
import { confirm } from '../../shared/confirm-dialog';
import { CURRENCY, HumanizePipe } from '../../shared/list';
import { OpportunityFormDialog } from './opportunity-form-dialog';

@Component({
  selector: 'app-opportunity-detail',
  imports: [CurrencyPipe, DatePipe, RouterLink, MatButtonModule, MatCardModule, MatIconModule, MatProgressBarModule, ActivityPanel, HumanizePipe],
  template: `
    @if (opportunity.isLoading() && !o()) { <mat-progress-bar mode="indeterminate" /> }
    @if (o(); as o) {
      <div class="page-header">
        <div>
          <a routerLink="/opportunities" class="back"><mat-icon inline>arrow_back</mat-icon> Opportunities</a>
          <h1>{{ o.name }}</h1>
          <div class="muted"><a [routerLink]="['/accounts', o.accountId]">{{ o.accountName }}</a> &middot; Owner: {{ o.ownerName }}</div>
        </div>
        <span class="spacer"></span>
        @if (auth.canModify(o.ownerId)) {
          <button mat-stroked-button (click)="edit(o)"><mat-icon>edit</mat-icon> Edit</button>
        }
        @if (auth.isManager()) {
          <button mat-stroked-button class="danger" (click)="remove(o)"><mat-icon>delete</mat-icon> Delete</button>
        }
      </div>

      <mat-card appearance="outlined" class="stage-bar">
        <div class="stages">
          @for (s of openStages; track s; let i = $index) {
            <button class="stage" [class.done]="stageIndex(o.stage) > i || o.stage === 'ClosedWon'" [class.current]="o.stage === s"
              [disabled]="!auth.canModify(o.ownerId)" (click)="setStage(o, s)">{{ s | humanize }}</button>
          }
        </div>
        @if (auth.canModify(o.ownerId)) {
          <div class="close-actions">
            <button mat-flat-button class="success" [disabled]="o.stage === 'ClosedWon'" (click)="setStage(o, 'ClosedWon')"><mat-icon>emoji_events</mat-icon> Won</button>
            <button mat-stroked-button class="danger" [disabled]="o.stage === 'ClosedLost'" (click)="markLost(o)"><mat-icon>block</mat-icon> Lost</button>
          </div>
        }
      </mat-card>

      <div class="detail-layout">
        <mat-card appearance="outlined" class="info">
          <mat-card-content>
            <dl class="fields">
              <dt>Stage</dt><dd><span class="chip" [attr.data-v]="o.stage">{{ o.stage | humanize }}</span></dd>
              <dt>Amount</dt><dd>{{ o.amount | currency: currency : 'symbol' : '1.0-0' }}</dd>
              <dt>Probability</dt><dd>{{ o.probability }}%</dd>
              <dt>Weighted</dt><dd>{{ (o.amount * o.probability) / 100 | currency: currency : 'symbol' : '1.0-0' }}</dd>
              <dt>Expected close</dt><dd>{{ o.expectedCloseDate | date: 'mediumDate' }}</dd>
              @if (o.closedAt) { <dt>Closed</dt><dd>{{ o.closedAt | date: 'medium' }}</dd> }
              @if (o.lossReason) { <dt>Loss reason</dt><dd>{{ o.lossReason }}</dd> }
              <dt>Contact</dt><dd>@if (o.contactId) { <a [routerLink]="['/contacts', o.contactId]">{{ o.contactName }}</a> }</dd>
              <dt>Created</dt><dd>{{ o.createdAt | date: 'medium' }}</dd>
              <dt>Updated</dt><dd>{{ o.updatedAt | date: 'medium' }}</dd>
            </dl>
            @if (o.description) { <p class="desc">{{ o.description }}</p> }
          </mat-card-content>
        </mat-card>
        <mat-card appearance="outlined"><mat-card-content>
          <app-activity-panel [links]="{ opportunityId: o.id, accountId: o.accountId, contactId: o.contactId }" />
        </mat-card-content></mat-card>
      </div>
    }
  `,
  styles: `
    .stage-bar { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; padding: 12px; margin-bottom: 16px; }
    .stages { display: flex; flex: 1; min-width: 320px; }
    .stage { flex: 1; border: 0; padding: 10px 8px; background: var(--mat-sys-surface-container-high); color: var(--mat-sys-on-surface);
      cursor: pointer; clip-path: polygon(0 0, calc(100% - 12px) 0, 100% 50%, calc(100% - 12px) 100%, 0 100%, 12px 50%); font: inherit; }
    .stage.done { background: var(--mat-sys-primary-container); }
    .stage.current { background: var(--mat-sys-primary); color: var(--mat-sys-on-primary); font-weight: 600; }
    .stage:disabled { cursor: default; }
    .close-actions { display: flex; gap: 8px; }
  `,
})
export class OpportunityDetailPage {
  readonly id = input.required<string>();
  readonly auth = inject(AuthService);
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  readonly currency = CURRENCY;
  readonly openStages = OPEN_STAGES;

  readonly opportunity = httpResource<OpportunityDetail>(() => `/api/opportunities/${this.id()}`);
  readonly o = computed(() => (this.opportunity.hasValue() ? this.opportunity.value() : undefined));

  stageIndex(stage: OpportunityStage): number {
    return OPEN_STAGES.indexOf(stage);
  }

  setStage(o: OpportunityDetail, stage: OpportunityStage, lossReason: string | null = null): void {
    if (o.stage === stage) return;
    this.api.changeStage(o.id, stage, lossReason).subscribe((updated) => {
      this.opportunity.set(updated);
      if (stage === 'ClosedWon') this.notify.success('Deal won!');
    });
  }

  markLost(o: OpportunityDetail): void {
    confirm(this.dialog, { title: 'Mark as lost?', message: 'Optionally record why this deal was lost.', confirmText: 'Mark lost', input: { label: 'Loss reason' } })
      .subscribe((reason) => reason !== undefined && this.setStage(o, 'ClosedLost', typeof reason === 'string' ? reason : null));
  }

  edit(o: OpportunityDetail): void {
    this.dialog.open(OpportunityFormDialog, { data: { item: o }, width: '720px' }).afterClosed()
      .subscribe((saved) => saved && this.opportunity.reload());
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
