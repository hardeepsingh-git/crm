import { CdkDragDrop, DragDropModule } from '@angular/cdk/drag-drop';
import { CurrencyPipe, DatePipe, NgTemplateOutlet } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, TemplateRef, computed, contentChild, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { AuthService } from '../core/auth.service';
import { MetadataService } from '../core/metadata.service';
import { AuditItem, StageDef } from '../core/models';
import { HumanizePipe } from './list';

/** Coloured chip for a pipeline stage, tinted with the stage's configured colour. */
@Component({
  selector: 'app-stage-chip',
  template: `<span class="chip" [style.--chip-fg]="def()?.color" [style.--chip-bg]="tint()">{{ def()?.label ?? stage() }}</span>`,
})
export class StageChip {
  readonly objectKey = input.required<string>();
  readonly stage = input.required<string>();
  private readonly meta = inject(MetadataService);
  readonly def = computed(() => this.meta.stage(this.objectKey(), this.stage()));
  readonly tint = computed(() => (this.def() ? `${this.def()!.color}1f` : null));
}

export interface BoardCard {
  id: string;
  stage: string;
  ownerId: string;
}

/** Chevron path through the open stages, plus buttons for each closed (won/lost) stage. */
@Component({
  selector: 'app-stage-path',
  imports: [MatButtonModule, MatCardModule, MatIconModule],
  template: `
    <mat-card appearance="outlined" class="stage-bar">
      <div class="stages">
        @for (s of open(); track s.key; let i = $index) {
          <button class="stage" [class.done]="isDone(i)" [class.current]="stage() === s.key"
            [disabled]="disabled()" (click)="pick(s)">{{ s.label }}</button>
        }
      </div>
      @if (!disabled()) {
        <div class="close-actions">
          @for (s of closed(); track s.key) {
            @if (s.category === 'Won') {
              <button mat-flat-button class="success" [disabled]="stage() === s.key" (click)="pick(s)"><mat-icon>emoji_events</mat-icon> {{ s.label }}</button>
            } @else {
              <button mat-stroked-button class="danger" [disabled]="stage() === s.key" (click)="pick(s)"><mat-icon>block</mat-icon> {{ s.label }}</button>
            }
          }
        </div>
      }
    </mat-card>
  `,
  styles: `
    .stage-bar { display: flex; flex-flow: row wrap; gap: 12px; align-items: center; padding: 12px; margin-bottom: 16px; }
    .stages { display: flex; flex: 1; min-width: 320px; }
    .stage { flex: 1; border: 0; padding: 10px 8px; background: var(--mat-sys-surface-container-high); color: var(--mat-sys-on-surface);
      cursor: pointer; clip-path: polygon(0 0, calc(100% - 12px) 0, 100% 50%, calc(100% - 12px) 100%, 0 100%, 12px 50%); font: inherit; }
    .stage.done { background: var(--mat-sys-primary-container); }
    .stage.current { background: var(--mat-sys-primary); color: var(--mat-sys-on-primary); font-weight: 600; }
    .stage:disabled { cursor: default; }
    .close-actions { display: flex; gap: 8px; }
  `,
})
export class StagePath {
  readonly objectKey = input.required<string>();
  readonly stage = input.required<string>();
  readonly disabled = input(false);
  readonly selected = output<StageDef>();
  private readonly meta = inject(MetadataService);
  readonly open = computed(() => this.meta.openStages(this.objectKey()));
  readonly closed = computed(() => this.meta.stages(this.objectKey()).filter((s) => s.category !== 'Open'));
  private readonly current = computed(() => this.meta.stage(this.objectKey(), this.stage()));

  isDone(index: number): boolean {
    const current = this.current();
    if (!current) return false;
    if (current.category === 'Won') return true;
    return this.open().findIndex((s) => s.key === current.key) > index;
  }

  pick(s: StageDef): void {
    if (s.key !== this.stage()) this.selected.emit(s);
  }
}

export interface BoardMove<T> {
  item: T;
  stage: StageDef;
}

/** Kanban board with one column per configured stage; the card body is supplied as an <ng-template>. */
@Component({
  selector: 'app-stage-board',
  imports: [CurrencyPipe, DragDropModule, NgTemplateOutlet],
  template: `
    <div class="board" cdkDropListGroup [style.grid-template-columns]="'repeat(' + columns().length + ', minmax(220px, 1fr))'">
      @for (col of columns(); track col.stage.key) {
        <section class="column" [style.--chip-fg]="col.stage.color">
          <header>
            <div class="title">{{ col.stage.label }} <span class="muted">({{ col.items.length }})</span></div>
            @if (amount()) {
              <div class="total">{{ col.total | currency: meta.currency() : 'symbol' : '1.0-0' }}</div>
            }
          </header>
          <div class="cards" cdkDropList [cdkDropListData]="col.stage" (cdkDropListDropped)="drop($event)">
            @for (item of col.items; track item.id) {
              <div class="card" cdkDrag [cdkDragData]="item" [cdkDragDisabled]="!auth.canModify(objectKey(), item.ownerId)">
                <ng-container *ngTemplateOutlet="card(); context: { $implicit: item }" />
              </div>
            }
          </div>
        </section>
      }
    </div>
  `,
  styles: `
    .board { display: grid; gap: 12px; overflow-x: auto; padding-bottom: 12px; }
    .column { background: var(--mat-sys-surface-container); border-radius: 12px; padding: 8px; min-height: 60vh; display: flex; flex-direction: column; }
    .column header { padding: 8px; border-bottom: 3px solid var(--chip-fg, var(--mat-sys-primary)); margin-bottom: 8px; }
    .title { font-weight: 600; }
    .cards { flex: 1; min-height: 80px; display: flex; flex-direction: column; gap: 8px; }
    .card { background: var(--mat-sys-surface); border: 1px solid var(--mat-sys-outline-variant); border-radius: 8px; padding: 10px; cursor: grab; }
    .cdk-drag-preview { box-shadow: var(--mat-sys-level3); }
    .cdk-drag-placeholder { opacity: .3; }
  `,
})
export class StageBoard<T extends BoardCard> {
  readonly objectKey = input.required<string>();
  readonly items = input.required<T[]>();
  /** Optional per-card value summed into each column header. */
  readonly amount = input<((item: T) => number) | null>(null);
  readonly moved = output<BoardMove<T>>();
  readonly card = contentChild.required<TemplateRef<{ $implicit: T }>>(TemplateRef);
  readonly auth = inject(AuthService);
  readonly meta = inject(MetadataService);

  readonly columns = computed(() => {
    const amount = this.amount();
    return this.meta.stages(this.objectKey()).map((stage) => {
      const items = this.items().filter((i) => i.stage === stage.key);
      return { stage, items, total: amount ? items.reduce((sum, i) => sum + amount(i), 0) : 0 };
    });
  });

  drop(event: CdkDragDrop<StageDef>): void {
    const item = event.item.data as T;
    if (item.stage !== event.container.data.key) this.moved.emit({ item, stage: event.container.data });
  }
}

/** Collapsible change history (audit trail) for a record; loads only when expanded. */
@Component({
  selector: 'app-record-history',
  imports: [DatePipe, MatExpansionModule, MatIconModule],
  template: `
    <mat-expansion-panel (opened)="open.set(true)">
      <mat-expansion-panel-header>
        <mat-panel-title><mat-icon>history</mat-icon>&nbsp;History</mat-panel-title>
      </mat-expansion-panel-header>
      @for (entry of history.value() ?? []; track entry.id) {
        <div class="entry">
          <div><strong>{{ entry.action }}</strong> by {{ entry.userName ?? 'System' }} <span class="muted">&middot; {{ entry.timestamp | date: 'medium' }}</span></div>
          @if (entry.action === 'Updated') {
            <ul>
              @for (c of changes(entry); track c.field) {
                <li><span class="field">{{ c.field }}</span>: <span class="old">{{ c.from }}</span> &rarr; {{ c.to }}</li>
              }
            </ul>
          }
        </div>
      } @empty {
        <p class="muted">{{ history.isLoading() ? 'Loading…' : 'No history recorded.' }}</p>
      }
    </mat-expansion-panel>
  `,
  styles: `
    .entry { padding: 8px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    ul { margin: 4px 0 0; padding-left: 20px; }
    .field { font-weight: 500; }
    .old { color: var(--mat-sys-on-surface-variant); text-decoration: line-through; }
  `,
})
export class RecordHistory {
  readonly entityType = input.required<string>();
  readonly entityId = input.required<string>();
  /** Bump to refresh after the record is edited. */
  readonly version = input(0);
  readonly open = signal(false);
  private readonly meta = inject(MetadataService);
  private readonly humanize = new HumanizePipe();

  readonly history = httpResource<AuditItem[]>(() => {
    this.version();
    return this.open() ? `/api/audit/${this.entityType()}/${this.entityId()}` : undefined;
  });

  changes(entry: AuditItem): { field: string; from: string; to: string }[] {
    return Object.entries(entry.changes).map(([key, [from, to]]) => ({
      field: this.fieldLabel(key),
      from: this.format(key, from),
      to: this.format(key, to),
    }));
  }

  private fieldLabel(key: string): string {
    if (key.startsWith('cf.')) {
      const f = this.meta.fields(this.entityType()).find((x) => x.key === key.slice(3));
      return f?.label ?? key.slice(3);
    }
    const text = this.humanize.transform(key);
    return text.charAt(0).toUpperCase() + text.slice(1);
  }

  private format(key: string, value: unknown): string {
    if (value === null || value === undefined || value === '') return '(empty)';
    if (Array.isArray(value)) return value.join(', ');
    if (key === 'stage') return this.meta.stage(this.entityType(), String(value))?.label ?? String(value);
    return String(value);
  }
}
