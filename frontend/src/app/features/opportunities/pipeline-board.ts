import { CurrencyPipe, DatePipe, LowerCasePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, inject, linkedSignal, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { RouterLink } from '@angular/router';
import { CrmApi, toParams } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { MetadataService } from '../../core/metadata.service';
import { OpportunityListItem } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { confirm } from '../../shared/confirm-dialog';
import { BoardMove, StageBoard } from '../../shared/record-widgets';
import { OpportunityFormDialog } from './opportunity-form-dialog';

@Component({
  selector: 'app-pipeline-board',
  imports: [CurrencyPipe, DatePipe, LowerCasePipe, RouterLink, MatButtonModule, MatIconModule, MatProgressBarModule, MatSlideToggleModule, StageBoard],
  template: `
    <div class="page-header">
      <h1>{{ meta.label('opportunity') }} pipeline</h1>
      <span class="spacer"></span>
      <mat-slide-toggle [checked]="mine()" (change)="mine.set($event.checked)">Mine only</mat-slide-toggle>
      <a mat-stroked-button routerLink="/opportunities"><mat-icon>list</mat-icon> List view</a>
      @if (auth.can('opportunity', 'create')) {
        <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> New {{ meta.label('opportunity') | lowercase }}</button>
      }
    </div>
    <p class="muted">Drag cards between columns to change stage. Closed columns show records closed in the last 30 days.</p>
    @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    <app-stage-board objectKey="opportunity" [items]="items()" [amount]="amount" (moved)="drop($event)">
      <ng-template let-o>
        <a class="name" [routerLink]="['/opportunities', o.id]">{{ o.name }}</a>
        <div class="muted">{{ o.accountName }}</div>
        <div class="row">
          <strong>{{ o.amount | currency: meta.currency() : 'symbol' : '1.0-0' }}</strong>
          <span class="muted">{{ o.expectedCloseDate | date: 'd MMM' }}</span>
        </div>
        <div class="muted small">{{ o.ownerName }}</div>
      </ng-template>
    </app-stage-board>
  `,
  styles: `
    .name { font-weight: 500; text-decoration: none; color: var(--mat-sys-on-surface); }
    .row { display: flex; justify-content: space-between; margin-top: 6px; }
    .small { font-size: 12px; }
  `,
})
export class PipelineBoard {
  readonly auth = inject(AuthService);
  readonly meta = inject(MetadataService);
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  readonly mine = signal(false);
  readonly amount = (o: OpportunityListItem) => o.amount;

  readonly data = httpResource<OpportunityListItem[]>(() => ({
    url: '/api/opportunities/pipeline',
    params: toParams({ mine: this.mine() }),
  }));
  /** Local copy so drag-and-drop can update optimistically. */
  readonly items = linkedSignal<OpportunityListItem[]>(() => (this.data.hasValue() ? this.data.value() : []));

  drop({ item, stage }: BoardMove<OpportunityListItem>): void {
    if (stage.category !== 'Lost') {
      this.move(item, stage.key, null, stage.category === 'Won');
      return;
    }
    confirm(this.dialog, {
      title: `Mark as ${stage.label}?`,
      message: `Why was "${item.name}" lost?`,
      confirmText: stage.label,
      input: { label: 'Loss reason' },
    }).subscribe((reason) => reason !== undefined && this.move(item, stage.key, typeof reason === 'string' ? reason : null, false));
  }

  private move(opp: OpportunityListItem, stage: string, lossReason: string | null, won: boolean): void {
    const previous = this.items();
    this.items.update((list) => list.map((o) => (o.id === opp.id ? { ...o, stage } : o)));
    this.api.changeStage(opp.id, stage, lossReason).subscribe({
      next: (updated) => {
        this.items.update((list) => list.map((o) => (o.id === opp.id ? { ...o, stage: updated.stage, probability: updated.probability } : o)));
        if (won) this.notify.success(`"${opp.name}" won!`);
      },
      error: () => this.items.set(previous),
    });
  }

  create(): void {
    this.dialog.open(OpportunityFormDialog, { data: null, width: '720px' }).afterClosed()
      .subscribe((saved) => saved && this.data.reload());
  }
}
