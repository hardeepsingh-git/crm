import { DatePipe } from '@angular/common';
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
import { RecordItem, StageDef } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { ActivityPanel } from '../../shared/activity-panel';
import { AttachmentsPanel } from '../../shared/attachments-panel';
import { confirm } from '../../shared/confirm-dialog';
import { CustomFieldsView } from '../../shared/custom-fields';
import { RecordHistory, StageChip, StagePath } from '../../shared/record-widgets';
import { RecordFormDialog } from './record-form-dialog';

@Component({
  selector: 'app-record-detail',
  imports: [DatePipe, RouterLink, MatButtonModule, MatCardModule, MatIconModule, MatProgressBarModule, ActivityPanel, AttachmentsPanel, CustomFieldsView, RecordHistory, StageChip, StagePath],
  template: `
    @if (record.isLoading() && !r()) { <mat-progress-bar mode="indeterminate" /> }
    @if (record.error()) { <p class="muted">Record not found or you do not have access to it.</p> }
    @if (r(); as r) {
      @let obj = object()!;
      <div class="page-header">
        <div>
          <a [routerLink]="['/o', obj.key]" class="back"><mat-icon inline>arrow_back</mat-icon> {{ obj.pluralLabel }}</a>
          <h1>{{ r.name }}</h1>
          <div class="muted">{{ obj.label }} &middot; Owner: {{ r.ownerName }}</div>
        </div>
        <span class="spacer"></span>
        @if (auth.canModify(obj.key, r.ownerId)) {
          <button mat-stroked-button (click)="edit(r)"><mat-icon>edit</mat-icon> Edit</button>
        }
        @if (auth.canDelete(obj.key, r.ownerId)) {
          <button mat-stroked-button class="danger" (click)="remove(r)"><mat-icon>delete</mat-icon> Delete</button>
        }
      </div>

      @if (obj.hasPipeline) {
        <app-stage-path [objectKey]="obj.key" [stage]="r.stage" [disabled]="!auth.canModify(obj.key, r.ownerId)" (selected)="setStage(r, $event)" />
      }

      <div class="detail-layout">
        <mat-card appearance="outlined" class="info">
          <mat-card-content>
            <dl class="fields">
              @if (obj.hasPipeline) {
                <dt>Stage</dt><dd><app-stage-chip [objectKey]="obj.key" [stage]="r.stage" /></dd>
                @if (r.closedAt) { <dt>Closed</dt><dd>{{ r.closedAt | date: 'medium' }}</dd> }
              }
              <app-custom-fields-view [fields]="meta.fields(obj.key)" [values]="r.customFields" />
              <dt>Owner</dt><dd>{{ r.ownerName }}</dd>
              <dt>Created</dt><dd>{{ r.createdAt | date: 'medium' }}</dd>
              <dt>Updated</dt><dd>{{ r.updatedAt | date: 'medium' }}</dd>
            </dl>
            <app-attachments-panel [objectKey]="obj.key" [recordId]="r.id" [ownerId]="r.ownerId" (changed)="version.set(version() + 1)" />
            <app-record-history [entityType]="obj.key" [entityId]="r.id" [version]="version()" />
          </mat-card-content>
        </mat-card>
        @if (auth.can('activity', 'read')) {
          <mat-card appearance="outlined"><mat-card-content>
            <app-activity-panel filterBy="customRecordId" [links]="{ customRecordId: r.id }" [customObjectKey]="obj.key" />
          </mat-card-content></mat-card>
        }
      </div>
    }
  `,
})
export class RecordDetailPage {
  readonly objectKey = input.required<string>();
  readonly id = input.required<string>();
  readonly auth = inject(AuthService);
  readonly meta = inject(MetadataService);
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  readonly version = signal(0);

  readonly object = computed(() => this.meta.object(this.objectKey()));
  readonly record = httpResource<RecordItem>(() => `/api/records/${this.objectKey()}/${this.id()}`);
  readonly r = computed(() => (this.record.hasValue() && this.object() ? this.record.value() : undefined));

  setStage(r: RecordItem, stage: StageDef): void {
    this.api.changeRecordStage(this.objectKey(), r.id, stage.key).subscribe((updated) => {
      this.record.set(updated);
      this.version.update((v) => v + 1);
    });
  }

  edit(r: RecordItem): void {
    this.dialog.open(RecordFormDialog, { data: { objectKey: this.objectKey(), item: r }, width: '720px' }).afterClosed().subscribe((saved) => {
      if (!saved) return;
      this.record.reload();
      this.version.update((v) => v + 1);
    });
  }

  remove(r: RecordItem): void {
    const label = this.object()?.label ?? 'record';
    confirm(this.dialog, {
      title: `Delete ${label.toLowerCase()}?`,
      message: `"${r.name}" and its activities will be deleted. This cannot be undone.`,
      confirmText: 'Delete',
      danger: true,
    }).subscribe((ok) => {
      if (!ok) return;
      this.api.records(this.objectKey()).delete(r.id).subscribe(() => {
        this.notify.success(`${label} deleted`);
        void this.router.navigate(['/o', this.objectKey()]);
      });
    });
  }
}
