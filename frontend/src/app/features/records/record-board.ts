import { DatePipe, LowerCasePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, effect, inject, input, linkedSignal, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { CrmApi, toParams } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { MetadataService } from '../../core/metadata.service';
import { RecordItem } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { CfFilters, CfValue, cfFromQuery } from '../../shared/custom-fields';
import { ListState } from '../../shared/list';
import { BoardMove, StageBoard } from '../../shared/record-widgets';
import { RecordFormDialog } from './record-form-dialog';

/** Kanban board for a custom object with a pipeline; the first currency field is totalled per column. */
@Component({
  selector: 'app-record-board',
  imports: [DatePipe, LowerCasePipe, RouterLink, MatButtonModule, MatIconModule, MatProgressBarModule, MatSlideToggleModule, StageBoard, CfValue, CfFilters],
  template: `
    @if (object(); as obj) {
      <div class="page-header">
        <h1>{{ obj.label }} board</h1>
        <span class="spacer"></span>
        <mat-slide-toggle [checked]="mine()" (change)="mine.set($event.checked)">Mine only</mat-slide-toggle>
        <a mat-stroked-button [routerLink]="['/o', obj.key]" [queryParams]="listQuery()"><mat-icon>list</mat-icon> List view</a>
        @if (auth.can(obj.key, 'create')) {
          <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> New {{ obj.label | lowercase }}</button>
        }
      </div>
      <div class="filters"><app-cf-filters [objectKey]="obj.key" [state]="state" /></div>
      <p class="muted">Drag cards between columns to change stage. Closed columns show records closed in the last 30 days.</p>
      @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      <app-stage-board [objectKey]="obj.key" [items]="items()" [amount]="amount()" (moved)="drop($event)">
        <ng-template let-r>
          <a class="name" [routerLink]="['/o', obj.key, r.id]">{{ r.name }}</a>
          @for (f of cardFields(); track f.key) {
            <div class="muted small">{{ f.label }}: <app-cf-value [field]="f" [value]="r.customFields[f.key]" /></div>
          }
          <div class="muted small">{{ r.ownerName }} &middot; {{ r.updatedAt | date: 'd MMM' }}</div>
        </ng-template>
      </app-stage-board>
    } @else {
      <p class="muted">This object has no pipeline.</p>
    }
  `,
  styles: `
    .name { font-weight: 500; text-decoration: none; color: var(--mat-sys-on-surface); display: block; margin-bottom: 4px; }
    .small { font-size: 12px; }
  `,
})
export class RecordBoard {
  readonly objectKey = input.required<string>();
  readonly auth = inject(AuthService);
  readonly meta = inject(MetadataService);
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  readonly mine = signal(false);
  readonly state = new ListState();
  private readonly route = inject(ActivatedRoute);
  readonly listQuery = computed(() => Object.fromEntries(Object.entries(this.state.cf()).map(([k, v]) => [`cf.${k}`, v])));

  readonly object = computed(() => {
    const o = this.meta.object(this.objectKey());
    return o && !o.isStandard && o.isActive && o.hasPipeline ? o : undefined;
  });
  readonly cardFields = computed(() => this.meta.listFields(this.objectKey()).slice(0, 3));
  readonly amount = computed(() => {
    const field = this.meta.fields(this.objectKey()).find((f) => f.type === 'Currency');
    return field ? (r: RecordItem) => Number(r.customFields[field.key] ?? 0) : null;
  });

  readonly data = httpResource<RecordItem[]>(() =>
    this.object() ? { url: `/api/records/${this.objectKey()}/board`, params: toParams({ mine: this.mine(), ...this.listQuery() }) } : undefined,
  );
  readonly items = linkedSignal<RecordItem[]>(() => (this.data.hasValue() ? this.data.value() : []));

  constructor() {
    effect(() => {
      this.objectKey();
      untracked(() => this.state.cf.set(cfFromQuery(this.route.snapshot.queryParamMap)));
    });
  }

  drop({ item, stage }: BoardMove<RecordItem>): void {
    const previous = this.items();
    this.items.update((list) => list.map((r) => (r.id === item.id ? { ...r, stage: stage.key } : r)));
    this.api.changeRecordStage(this.objectKey(), item.id, stage.key).subscribe({
      next: (updated) => {
        this.items.update((list) => list.map((r) => (r.id === item.id ? updated : r)));
        if (stage.category === 'Won') this.notify.success(`"${item.name}" moved to ${stage.label}`);
      },
      error: () => this.items.set(previous),
    });
  }

  create(): void {
    this.dialog.open(RecordFormDialog, { data: { objectKey: this.objectKey(), defaults: { customFields: this.state.cf() } }, width: '720px' })
      .afterClosed().subscribe((saved) => saved && this.data.reload());
  }
}
