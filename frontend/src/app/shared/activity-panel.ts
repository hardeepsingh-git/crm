import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { CrmApi, stableValue, toParams } from '../core/api';
import { AuthService } from '../core/auth.service';
import { MetadataService } from '../core/metadata.service';
import { ActivityItem, ActivityLinks, ActivityType, PagedResult } from '../core/models';
import { ActivityFormDialog, ActivityFormData } from '../features/activities/activity-form-dialog';

export const ACTIVITY_ICONS: Record<ActivityType, string> = {
  Task: 'task_alt',
  Call: 'call',
  Meeting: 'groups',
  Email: 'mail',
  Note: 'sticky_note_2',
};

export function isOverdue(a: ActivityItem): boolean {
  return !a.completedAt && !!a.dueAt && new Date(a.dueAt) < new Date();
}

/** Timeline of activities linked to a record, embedded on detail pages. */
@Component({
  selector: 'app-activity-panel',
  imports: [DatePipe, MatButtonModule, MatCheckboxModule, MatIconModule, MatTooltipModule],
  template: `
    <div class="panel-header">
      <h3>{{ meta.label('activity', true) }}</h3>
      <span class="spacer"></span>
      @if (auth.can('activity', 'create')) {
        <button mat-stroked-button (click)="add('Call')"><mat-icon>call</mat-icon> Log call</button>
        <button mat-stroked-button (click)="add('Note')"><mat-icon>sticky_note_2</mat-icon> Note</button>
        <button mat-flat-button (click)="add('Task')"><mat-icon>add</mat-icon> Task</button>
      }
    </div>
    @for (a of items(); track a.id) {
      <div class="activity-row" [class.done]="a.completedAt" [class.overdue]="overdue(a)">
        @if (a.type === 'Note') {
          <mat-icon class="type-icon">{{ icons[a.type] }}</mat-icon>
        } @else {
          <mat-checkbox [checked]="!!a.completedAt" [disabled]="!auth.canModify('activity', a.ownerId)" (change)="toggle(a, $event.checked)"
            [matTooltip]="a.completedAt ? 'Reopen' : 'Mark complete'" />
        }
        <div class="body" (click)="edit(a)" (keydown.enter)="edit(a)" tabindex="0" role="button">
          <div class="subject"><mat-icon inline>{{ icons[a.type] }}</mat-icon> {{ a.subject }}</div>
          <div class="meta muted">
            {{ a.type }} &middot; {{ a.ownerName }}
            @if (a.dueAt) { &middot; due {{ a.dueAt | date: 'd MMM y, h:mm a' }} }
            @if (a.completedAt) { &middot; done {{ a.completedAt | date: 'd MMM y' }} }
          </div>
          @if (a.description) {
            <div class="desc">{{ a.description }}</div>
          }
        </div>
      </div>
    } @empty {
      <p class="muted">No activities yet.</p>
    }
  `,
  styles: `
    .panel-header { display: flex; gap: 8px; align-items: center; margin-bottom: 8px; }
    .activity-row { display: flex; gap: 8px; align-items: flex-start; padding: 8px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .activity-row.done .subject { text-decoration: line-through; opacity: .7; }
    .activity-row.overdue .meta { color: var(--mat-sys-error); }
    .type-icon { margin: 8px 12px; color: var(--mat-sys-on-surface-variant); }
    .body { flex: 1; cursor: pointer; padding-top: 8px; }
    .subject { font-weight: 500; }
    .desc { white-space: pre-wrap; margin-top: 4px; }
  `,
})
export class ActivityPanel {
  readonly links = input.required<ActivityLinks>();
  /** Which link identifies the page's record; the others only pre-fill new activities. */
  readonly filterBy = input.required<keyof ActivityLinks>();
  /** Object key when the panel is on a custom record page. */
  readonly customObjectKey = input<string | null>(null);
  readonly auth = inject(AuthService);
  readonly meta = inject(MetadataService);
  private readonly api = inject(CrmApi);
  private readonly dialog = inject(MatDialog);
  readonly icons = ACTIVITY_ICONS;
  readonly overdue = isOverdue;

  private readonly data = httpResource<PagedResult<ActivityItem>>(() => ({
    url: '/api/activities',
    params: toParams({ [this.filterBy()]: this.links()[this.filterBy()], pageSize: 100, sortBy: 'createdAt', sortDir: 'desc' }),
  }));
  private readonly value = stableValue(this.data);
  readonly items = computed(() => this.value()?.items ?? []);

  add(type: ActivityType): void {
    this.open({ links: this.links(), type, customObjectKey: this.customObjectKey() });
  }

  edit(item: ActivityItem): void {
    if (this.auth.canModify('activity', item.ownerId)) this.open({ item });
  }

  toggle(item: ActivityItem, completed: boolean): void {
    this.api.setActivityCompleted(item.id, completed).subscribe(() => this.data.reload());
  }

  private open(data: ActivityFormData): void {
    this.dialog
      .open(ActivityFormDialog, { data, width: '640px' })
      .afterClosed()
      .subscribe((saved) => saved && this.data.reload());
  }
}
