import { DatePipe, LowerCasePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { MetadataService } from '../../core/metadata.service';
import { FieldDef, ObjectDef, PagedResult, RecordItem } from '../../core/models';
import { CfValue } from '../../shared/custom-fields';
import { StageChip } from '../../shared/record-widgets';
import { RecordFormDialog, nameSourceField } from './record-form-dialog';

export interface Relation {
  object: ObjectDef;
  field: FieldDef;
}

/** Custom objects with a lookup to the given object that the user can read, e.g. applications for a candidate. */
export function relationsTo(objectKey: string, objects: ObjectDef[], canRead: (key: string) => boolean): Relation[] {
  return objects
    .filter((o) => !o.isStandard && o.isActive && canRead(o.key))
    .flatMap((o) => o.fields.filter((f) => f.type === 'Lookup' && f.lookupObjectKey === objectKey).map((field) => ({ object: o, field })));
}

const PAGE = 10;

/** One related list: the newest records pointing at this record, with links to the full filtered list and board. */
@Component({
  selector: 'app-related-list',
  imports: [DatePipe, LowerCasePipe, RouterLink, MatButtonModule, MatIconModule, MatProgressBarModule, CfValue, StageChip],
  template: `
    @let o = relation().object;
    <div class="head">
      <h3><mat-icon inline>{{ o.icon }}</mat-icon> {{ title() }} <span class="muted">({{ total() }})</span></h3>
      <span class="spacer"></span>
      @if (o.hasPipeline && total() > 0) {
        <a mat-button [routerLink]="['/o', o.key, 'board']" [queryParams]="query()"><mat-icon>view_kanban</mat-icon> Pipeline</a>
      }
      @if (total() > rows().length) {
        <a mat-button [routerLink]="['/o', o.key]" [queryParams]="query()">View all</a>
      }
      @if (auth.can(o.key, 'create')) {
        <button mat-stroked-button (click)="create()"><mat-icon>add</mat-icon> New {{ o.label | lowercase }}</button>
      }
    </div>
    @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @for (r of rows(); track r.id) {
      <div class="row">
        <a class="name" [routerLink]="['/o', o.key, r.id]">{{ r.name }}</a>
        @for (f of columns(); track f.key) {
          <span class="cell"><app-cf-value [field]="f" [value]="r.customFields[f.key]" /></span>
        }
        @if (o.hasPipeline) { <app-stage-chip [objectKey]="o.key" [stage]="r.stage" /> }
        <span class="muted small">{{ r.updatedAt | date: 'd MMM' }}</span>
      </div>
    } @empty {
      @if (!data.isLoading()) { <p class="muted small">None yet.</p> }
    }
  `,
  styles: `
    :host { display: block; padding: 8px 0; }
    .head { display: flex; align-items: center; gap: 4px; flex-wrap: wrap; }
    h3 { margin: 0; font-size: 1rem; }
    .row { display: flex; align-items: center; gap: 12px; padding: 6px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); flex-wrap: wrap; }
    .name { font-weight: 500; flex: 1 1 160px; }
    .cell { flex: 0 1 180px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .small { font-size: 0.85em; margin: 4px 0; }
  `,
})
export class RelatedList {
  readonly relation = input.required<Relation>();
  readonly recordId = input.required<string>();
  readonly recordName = input('');
  readonly auth = inject(AuthService);
  private readonly meta = inject(MetadataService);
  private readonly dialog = inject(MatDialog);

  readonly query = computed(() => ({ [`cf.${this.relation().field.key}`]: this.recordId() }));
  readonly data = httpResource<PagedResult<RecordItem>>(() => ({
    url: `/api/records/${this.relation().object.key}`,
    params: { ...this.query(), pageSize: PAGE, sortBy: 'updatedAt', sortDir: 'desc' },
  }));
  readonly rows = computed(() => (this.data.hasValue() ? this.data.value().items : []));
  readonly total = computed(() => (this.data.hasValue() ? this.data.value().totalCount : 0));
  /** With several lookups to the same object, the field label tells the lists apart. */
  readonly title = computed(() => {
    const { object, field } = this.relation();
    const siblings = object.fields.filter((f) => f.type === 'Lookup' && f.lookupObjectKey === field.lookupObjectKey);
    return siblings.length > 1 ? `${object.pluralLabel} (${field.label})` : object.pluralLabel;
  });
  /** The record's other list columns, e.g. the job on a candidate's applications. */
  readonly columns = computed(() =>
    this.relation().object.fields.filter((f) => f.showInList && f.key !== this.relation().field.key).slice(0, 2),
  );

  create(): void {
    const { object, field } = this.relation();
    const fromThisRecord = nameSourceField(object, object.fields, (key) => this.meta.label(key))?.key === field.key;
    this.dialog
      .open(RecordFormDialog, {
        data: {
          objectKey: object.key,
          defaults: { customFields: { [field.key]: this.recordId() }, name: fromThisRecord ? this.recordName() : undefined },
        },
        width: '720px',
      })
      .afterClosed()
      .subscribe((saved?: RecordItem) => saved && this.data.reload());
  }
}

/** All related lists for a record, or nothing when no object links to it. */
@Component({
  selector: 'app-related-records',
  imports: [MatCardModule, RelatedList],
  template: `
    @for (r of relations(); track r.object.key + '.' + r.field.key) {
      <mat-card appearance="outlined" class="related"><mat-card-content>
        <app-related-list [relation]="r" [recordId]="recordId()" [recordName]="recordName()" />
      </mat-card-content></mat-card>
    }
  `,
  styles: `:host { display: block; } .related { margin-top: 16px; }`,
})
export class RelatedRecords {
  readonly objectKey = input.required<string>();
  readonly recordId = input.required<string>();
  readonly recordName = input('');
  private readonly meta = inject(MetadataService);
  private readonly auth = inject(AuthService);
  readonly relations = computed(() => relationsTo(this.objectKey(), this.meta.objects(), (key) => this.auth.can(key, 'read')));
}
