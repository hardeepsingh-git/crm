import { DatePipe, LowerCasePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSortModule } from '@angular/material/sort';
import { MatTableModule } from '@angular/material/table';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { stableValue } from '../../core/api';
import { AuthService } from '../../core/auth.service';
import { MetadataService } from '../../core/metadata.service';
import { PagedResult, RecordItem } from '../../core/models';
import { CfFilters, CfValue, cfFromQuery } from '../../shared/custom-fields';
import { ExportButton } from '../../shared/export-button';
import { ListState, inputValue } from '../../shared/list';
import { StageChip } from '../../shared/record-widgets';
import { RecordFormDialog } from './record-form-dialog';
import { ImportDialog } from './import-dialog';

/** List page for any tenant-defined object: columns and filters come from its field definitions. */
@Component({
  selector: 'app-record-list',
  imports: [DatePipe, LowerCasePipe, RouterLink, MatTableModule, MatPaginatorModule, MatSortModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatIconModule, MatButtonModule, MatButtonToggleModule, MatSlideToggleModule, MatProgressBarModule, CfValue, CfFilters, ExportButton, StageChip],
  template: `
    @if (object(); as obj) {
      <div class="page-header">
        <h1><mat-icon class="title-icon">{{ obj.icon }}</mat-icon> {{ obj.pluralLabel }}</h1>
        <span class="spacer"></span>
        @if (obj.hasPipeline) {
          <a mat-stroked-button [routerLink]="['/o', obj.key, 'board']" [queryParams]="boardQuery()"><mat-icon>view_kanban</mat-icon> Board</a>
        }
        @if (auth.can(obj.key, 'create')) {
          <button mat-stroked-button (click)="importCsv()"><mat-icon>upload_file</mat-icon> Import</button>
        }
        <app-export-button [objectKey]="obj.key" [params]="exportParams()" />
        @if (auth.can(obj.key, 'create')) {
          <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> New {{ obj.label | lowercase }}</button>
        }
      </div>
      @if (obj.description) { <p class="muted">{{ obj.description }}</p> }
      <div class="filters">
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="search">
          <mat-label>Search {{ obj.nameLabel | lowercase }}</mat-label>
          <input matInput [value]="state.search()" (input)="state.setSearch(inputValue($event))" />
          <mat-icon matSuffix>search</mat-icon>
        </mat-form-field>
        @if (obj.hasPipeline) {
          <mat-button-toggle-group [value]="open()" (change)="open.set($event.value); state.resetPage()" hideSingleSelectionIndicator>
            <mat-button-toggle [value]="true">Open</mat-button-toggle>
            <mat-button-toggle [value]="null">All</mat-button-toggle>
          </mat-button-toggle-group>
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Stage</mat-label>
            <mat-select [value]="stage()" (selectionChange)="stage.set($event.value); state.resetPage()">
              <mat-option [value]="null">All</mat-option>
              @for (s of obj.stages; track s.key) { <mat-option [value]="s.key">{{ s.label }}</mat-option> }
            </mat-select>
          </mat-form-field>
        }
        <app-cf-filters [objectKey]="obj.key" [state]="state" />
        <mat-slide-toggle [checked]="state.mine()" (change)="state.setMine($event.checked)">Mine only</mat-slide-toggle>
      </div>
      <div class="table-wrap">
        @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
        <table mat-table [dataSource]="rows()" matSort (matSortChange)="state.onSort($event)">
          <ng-container matColumnDef="name">
            <th mat-header-cell *matHeaderCellDef mat-sort-header>{{ obj.nameLabel }}</th>
            <td mat-cell *matCellDef="let r"><a [routerLink]="['/o', obj.key, r.id]">{{ r.name }}</a></td>
          </ng-container>
          <ng-container matColumnDef="stage">
            <th mat-header-cell *matHeaderCellDef mat-sort-header>Stage</th>
            <td mat-cell *matCellDef="let r"><app-stage-chip [objectKey]="obj.key" [stage]="r.stage" /></td>
          </ng-container>
          @for (f of cfFields(); track f.key) {
            <ng-container [matColumnDef]="'cf.' + f.key">
              <th mat-header-cell *matHeaderCellDef>{{ f.label }}</th>
              <td mat-cell *matCellDef="let r"><app-cf-value [field]="f" [value]="r.customFields[f.key]" /></td>
            </ng-container>
          }
          <ng-container matColumnDef="owner">
            <th mat-header-cell *matHeaderCellDef>Owner</th>
            <td mat-cell *matCellDef="let r">{{ r.ownerName }}</td>
          </ng-container>
          <ng-container matColumnDef="createdAt">
            <th mat-header-cell *matHeaderCellDef mat-sort-header>Created</th>
            <td mat-cell *matCellDef="let r">{{ r.createdAt | date: 'mediumDate' }}</td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="columns()"></tr>
          <tr mat-row *matRowDef="let row; columns: columns()" class="clickable" (click)="openRow(row)"></tr>
          <tr class="mat-mdc-row" *matNoDataRow><td class="mat-mdc-cell empty" [attr.colspan]="columns().length">No records found.</td></tr>
        </table>
        <mat-paginator [length]="total()" [pageIndex]="state.page()" [pageSize]="state.pageSize()" [pageSizeOptions]="[10, 20, 50, 100]"
          (page)="state.onPage($event)" />
      </div>
    } @else {
      <p class="muted">This object does not exist or is inactive.</p>
    }
  `,
  styles: `.title-icon { vertical-align: middle; }`,
})
export class RecordList {
  readonly objectKey = input.required<string>();
  readonly auth = inject(AuthService);
  readonly meta = inject(MetadataService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  readonly inputValue = inputValue;
  readonly state = new ListState();
  readonly stage = signal<string | null>(null);
  readonly open = signal<boolean | null>(true);

  readonly object = computed(() => {
    const o = this.meta.object(this.objectKey());
    return o && !o.isStandard && o.isActive ? o : undefined;
  });
  readonly cfFields = computed(() => this.meta.listFields(this.objectKey()));
  /** Carries lookup filters to the board, so "applications for this job" stays filtered. */
  readonly boardQuery = computed(() => Object.fromEntries(Object.entries(this.state.cf()).map(([k, v]) => [`cf.${k}`, v])));
  readonly columns = computed(() => [
    'name', ...(this.object()?.hasPipeline ? ['stage'] : []), ...this.cfFields().map((f) => `cf.${f.key}`), 'owner', 'createdAt',
  ]);

  readonly data = httpResource<PagedResult<RecordItem>>(() =>
    this.object()
      ? {
          url: `/api/records/${this.objectKey()}`,
          params: this.exportParams(),
        }
      : undefined,
  );
  readonly exportParams = computed(() => this.state.params(this.object()?.hasPipeline ? { stage: this.stage(), open: this.open() } : {}));
  private readonly value = stableValue(this.data);
  readonly rows = computed(() => this.value()?.items ?? []);
  readonly total = computed(() => this.value()?.totalCount ?? 0);

  constructor() {
    // The component is reused when navigating between objects; filters come from the link (e.g. "View all" on a related list).
    let previous: string | undefined;
    effect(() => {
      const key = this.objectKey();
      untracked(() => {
        if (previous !== undefined && previous !== key) {
          this.state.page.set(0);
          this.stage.set(null);
        }
        this.state.cf.set(cfFromQuery(this.route.snapshot.queryParamMap));
      });
      previous = key;
    });
  }

  openRow(row: RecordItem): void {
    void this.router.navigate(['/o', this.objectKey(), row.id]);
  }

  create(): void {
    this.dialog
      .open(RecordFormDialog, { data: { objectKey: this.objectKey() }, width: '720px' })
      .afterClosed()
      .subscribe((saved?: RecordItem) => saved && this.router.navigate(['/o', this.objectKey(), saved.id]));
  }

  importCsv(): void {
    this.dialog
      .open(ImportDialog, { data: { objectKey: this.objectKey() }, width: '760px', disableClose: true })
      .afterClosed()
      .subscribe((created?: number) => created && this.data.reload());
  }
}
