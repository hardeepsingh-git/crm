import { CdkDragDrop, DragDropModule } from '@angular/cdk/drag-drop';
import { CurrencyPipe, DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, linkedSignal, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { RouterLink } from '@angular/router';
import { CrmApi, toParams } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { OpportunityListItem, OpportunityStage, STAGES } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { confirm } from '../../shared/confirm-dialog';
import { CURRENCY, HumanizePipe } from '../../shared/list';
import { OpportunityFormDialog } from './opportunity-form-dialog';

@Component({
  selector: 'app-pipeline-board',
  imports: [CurrencyPipe, DatePipe, RouterLink, DragDropModule, MatButtonModule, MatIconModule, MatProgressBarModule, MatSlideToggleModule, HumanizePipe],
  template: `
    <div class="page-header">
      <h1>Pipeline</h1>
      <span class="spacer"></span>
      <mat-slide-toggle [checked]="mine()" (change)="mine.set($event.checked)">My deals</mat-slide-toggle>
      <a mat-stroked-button routerLink="/opportunities"><mat-icon>list</mat-icon> List view</a>
      <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> New opportunity</button>
    </div>
    <p class="muted">Drag cards between columns to change stage. Closed columns show deals closed in the last 30 days.</p>
    @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    <div class="board" cdkDropListGroup>
      @for (col of columns(); track col.stage) {
        <section class="column" [attr.data-v]="col.stage">
          <header>
            <div class="title">{{ col.stage | humanize }} <span class="muted">({{ col.items.length }})</span></div>
            <div class="total">{{ col.total | currency: currency : 'symbol' : '1.0-0' }}</div>
          </header>
          <div class="cards" cdkDropList [cdkDropListData]="col.stage" (cdkDropListDropped)="drop($event)">
            @for (o of col.items; track o.id) {
              <div class="card" cdkDrag [cdkDragData]="o" [cdkDragDisabled]="!auth.canModify(o.ownerId)">
                <a class="name" [routerLink]="['/opportunities', o.id]">{{ o.name }}</a>
                <div class="muted">{{ o.accountName }}</div>
                <div class="row">
                  <strong>{{ o.amount | currency: currency : 'symbol' : '1.0-0' }}</strong>
                  <span class="muted">{{ o.expectedCloseDate | date: 'd MMM' }}</span>
                </div>
                <div class="muted small">{{ o.ownerName }}</div>
              </div>
            }
          </div>
        </section>
      }
    </div>
  `,
  styles: `
    .board { display: grid; grid-template-columns: repeat(6, minmax(220px, 1fr)); gap: 12px; overflow-x: auto; padding-bottom: 12px; }
    .column { background: var(--mat-sys-surface-container); border-radius: 12px; padding: 8px; min-height: 60vh; display: flex; flex-direction: column; }
    .column header { padding: 8px; border-bottom: 3px solid var(--chip-fg, var(--mat-sys-primary)); margin-bottom: 8px; }
    .title { font-weight: 600; }
    .cards { flex: 1; min-height: 80px; display: flex; flex-direction: column; gap: 8px; }
    .card { background: var(--mat-sys-surface); border: 1px solid var(--mat-sys-outline-variant); border-radius: 8px; padding: 10px; cursor: grab; }
    .card .name { font-weight: 500; text-decoration: none; color: var(--mat-sys-on-surface); }
    .row { display: flex; justify-content: space-between; margin-top: 6px; }
    .small { font-size: 12px; }
    .cdk-drag-preview { box-shadow: var(--mat-sys-level3); }
    .cdk-drag-placeholder { opacity: .3; }
  `,
})
export class PipelineBoard {
  readonly auth = inject(AuthService);
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  readonly currency = CURRENCY;
  readonly mine = signal(false);

  readonly data = httpResource<OpportunityListItem[]>(() => ({
    url: '/api/opportunities/pipeline',
    params: toParams({ mine: this.mine() }),
  }));
  /** Local copy so drag-and-drop can update optimistically. */
  private readonly items = linkedSignal<OpportunityListItem[]>(() => (this.data.hasValue() ? this.data.value() : []));

  readonly columns = computed(() =>
    STAGES.map((stage) => {
      const items = this.items().filter((o) => o.stage === stage);
      return { stage, items, total: items.reduce((sum, o) => sum + o.amount, 0) };
    }),
  );

  drop(event: CdkDragDrop<OpportunityStage>): void {
    const opp = event.item.data as OpportunityListItem;
    const stage = event.container.data;
    if (opp.stage === stage) return;

    if (stage === 'ClosedLost') {
      confirm(this.dialog, {
        title: 'Mark as lost?',
        message: `Why was "${opp.name}" lost?`,
        confirmText: 'Mark lost',
        input: { label: 'Loss reason' },
      }).subscribe((reason) => reason !== undefined && this.move(opp, stage, typeof reason === 'string' ? reason : null));
      return;
    }
    this.move(opp, stage, null);
  }

  private move(opp: OpportunityListItem, stage: OpportunityStage, lossReason: string | null): void {
    const previous = this.items();
    this.items.update((list) => list.map((o) => (o.id === opp.id ? { ...o, stage } : o)));
    this.api.changeStage(opp.id, stage, lossReason).subscribe({
      next: (updated) => {
        this.items.update((list) => list.map((o) => (o.id === opp.id ? { ...o, stage: updated.stage, probability: updated.probability } : o)));
        if (stage === 'ClosedWon') this.notify.success(`"${opp.name}" won!`);
      },
      error: () => this.items.set(previous),
    });
  }

  create(): void {
    this.dialog.open(OpportunityFormDialog, { data: null, width: '720px' }).afterClosed()
      .subscribe((saved) => saved && this.data.reload());
  }
}
