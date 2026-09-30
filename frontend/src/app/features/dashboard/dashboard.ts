import { CurrencyPipe, DatePipe, DecimalPipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { RouterLink } from '@angular/router';
import { stableValue, toParams } from '../../core/api';
import { MetadataService } from '../../core/metadata.service';
import { DashboardSummary, StageSummary } from '../../core/models';
import { ACTIVITY_ICONS, isOverdue } from '../../shared/activity-panel';
import { HumanizePipe } from '../../shared/list';

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

@Component({
  selector: 'app-dashboard',
  imports: [CurrencyPipe, DatePipe, DecimalPipe, RouterLink, MatCardModule, MatIconModule, MatProgressBarModule, MatSlideToggleModule, HumanizePipe],
  template: `
    <div class="page-header">
      <h1>{{ meta.tenantName() || 'Dashboard' }}</h1>
      <span class="spacer"></span>
      <mat-slide-toggle [checked]="mine()" (change)="mine.set($event.checked)">Only my records</mat-slide-toggle>
    </div>
    @if (data.isLoading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (summary(); as s) {
      <section class="kpis">
        @if (has(s, 'opportunity')) {
        <mat-card appearance="outlined" class="kpi"><div class="label">Open pipeline</div>
          <div class="value">{{ s.openPipelineAmount | currency: currency() : 'symbol' : '1.0-0' }}</div>
          <div class="muted">{{ s.openOpportunityCount }} open {{ meta.label('opportunity', true).toLowerCase() }}</div></mat-card>
        <mat-card appearance="outlined" class="kpi"><div class="label">Weighted forecast</div>
          <div class="value">{{ s.weightedPipelineAmount | currency: currency() : 'symbol' : '1.0-0' }}</div>
          <div class="muted">amount &times; probability</div></mat-card>
        <mat-card appearance="outlined" class="kpi"><div class="label">Won this month</div>
          <div class="value">{{ s.wonThisMonthAmount | currency: currency() : 'symbol' : '1.0-0' }}</div>
          <div class="muted">{{ s.wonThisMonthCount }} won</div></mat-card>
        <mat-card appearance="outlined" class="kpi"><div class="label">Win rate (90 days)</div>
          <div class="value">{{ s.winRate | number: '1.0-1' }}%</div>
          <div class="muted">won / closed</div></mat-card>
        }
        @if (has(s, 'lead')) {
        <mat-card appearance="outlined" class="kpi"><div class="label">Open {{ meta.label('lead', true).toLowerCase() }}</div>
          <div class="value"><a routerLink="/leads">{{ s.openLeadCount }}</a></div>
          <div class="muted">not yet converted</div></mat-card>
        }
        @if (has(s, 'account') || has(s, 'contact')) {
        <mat-card appearance="outlined" class="kpi"><div class="label">Relationships</div>
          <div class="value">{{ s.accountCount }}</div>
          <div class="muted">{{ meta.label('account', true).toLowerCase() }} &middot; {{ s.contactCount }} {{ meta.label('contact', true).toLowerCase() }}</div></mat-card>
        }
        @for (p of s.customPipelines; track p.objectKey) {
        <mat-card appearance="outlined" class="kpi"><div class="label"><mat-icon inline>{{ p.icon }}</mat-icon> Open {{ p.label.toLowerCase() }}</div>
          <div class="value"><a [routerLink]="['/o', p.objectKey]">{{ p.openCount }}</a></div>
          <div class="muted"><a [routerLink]="['/o', p.objectKey, 'board']">view board</a></div></mat-card>
        }
        @if (has(s, 'activity')) {
        <mat-card appearance="outlined" class="kpi" [class.alert]="s.overdueActivityCount > 0"><div class="label">Overdue {{ meta.label('activity', true).toLowerCase() }}</div>
          <div class="value"><a routerLink="/activities" [queryParams]="{ status: 'Overdue' }">{{ s.overdueActivityCount }}</a></div>
          <div class="muted">need attention</div></mat-card>
        }
      </section>

      <section class="grid">
        @if (has(s, 'opportunity')) {
        <mat-card appearance="outlined">
          <mat-card-header><mat-card-title>{{ meta.label('opportunity', true) }} by stage</mat-card-title></mat-card-header>
          <mat-card-content>
            @for (st of s.pipelineByStage; track st.stage) {
              <div class="bar-row">
                <div class="bar-label">{{ st.label }} <span class="muted">({{ st.count }})</span></div>
                <div class="bar-track"><div class="bar" [style.background]="st.color" [style.width.%]="pct(st.amount, maxStage())"></div></div>
                <div class="bar-value">{{ st.amount | currency: currency() : 'symbol' : '1.0-0' }}</div>
              </div>
            }
          </mat-card-content>
        </mat-card>

        <mat-card appearance="outlined">
          <mat-card-header><mat-card-title>Won revenue (6 months)</mat-card-title></mat-card-header>
          <mat-card-content>
            <div class="columns">
              @for (m of s.wonByMonth; track $index) {
                <div class="col">
                  <div class="col-value">{{ m.amount | currency: currency() : 'symbol' : '1.0-0' }}</div>
                  <div class="col-track"><div class="col-bar" [style.height.%]="pct(m.amount, maxMonth())"></div></div>
                  <div class="muted">{{ monthName(m.month) }}</div>
                </div>
              }
            </div>
          </mat-card-content>
        </mat-card>
        }

        @for (p of s.customPipelines; track p.objectKey) {
        <mat-card appearance="outlined">
          <mat-card-header><mat-card-title>{{ p.label }} by stage</mat-card-title></mat-card-header>
          <mat-card-content>
            @for (st of p.stages; track st.stage) {
              <div class="bar-row">
                <div class="bar-label">{{ st.label }}</div>
                <div class="bar-track"><div class="bar" [style.background]="st.color" [style.width.%]="pct(st.count, maxCount(p.stages))"></div></div>
                <div class="bar-value">{{ st.count }}</div>
              </div>
            }
          </mat-card-content>
        </mat-card>
        }

        @if (has(s, 'lead')) {
        <mat-card appearance="outlined">
          <mat-card-header><mat-card-title>{{ meta.label('lead', true) }} by source</mat-card-title></mat-card-header>
          <mat-card-content>
            @for (src of s.leadsBySource; track src.source) {
              <div class="bar-row">
                <div class="bar-label">{{ src.source | humanize }}</div>
                <div class="bar-track"><div class="bar alt" [style.width.%]="pct(src.count, maxSource())"></div></div>
                <div class="bar-value">{{ src.count }}</div>
              </div>
            } @empty {
              <p class="muted">No leads yet.</p>
            }
          </mat-card-content>
        </mat-card>
        }

        @if (has(s, 'activity')) {
        <mat-card appearance="outlined">
          <mat-card-header><mat-card-title>My upcoming {{ meta.label('activity', true).toLowerCase() }} (7 days)</mat-card-title></mat-card-header>
          <mat-card-content>
            @for (a of s.upcomingActivities; track a.id) {
              <div class="upcoming" [class.overdue]="overdue(a)">
                <mat-icon>{{ icons[a.type] }}</mat-icon>
                <div>
                  <div>{{ a.subject }}</div>
                  <div class="muted small">
                    {{ a.dueAt | date: 'EEE d MMM, h:mm a' }}
                    @if (a.accountName) { &middot; {{ a.accountName }} }
                  </div>
                </div>
              </div>
            } @empty {
              <p class="muted">Nothing due. Nice work!</p>
            }
          </mat-card-content>
        </mat-card>
        }
      </section>
    }
  `,
  styles: `
    .kpis { display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: 16px; margin-bottom: 16px; }
    .kpi { padding: 16px; }
    .kpi .label { font: var(--mat-sys-label-large); color: var(--mat-sys-on-surface-variant); }
    .kpi .value { font: var(--mat-sys-headline-medium); margin: 4px 0; }
    .kpi .value a { color: inherit; text-decoration: none; }
    .kpi.alert .value { color: var(--mat-sys-error); }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(420px, 1fr)); gap: 16px; }
    .bar-row { display: grid; grid-template-columns: 140px 1fr 110px; gap: 8px; align-items: center; margin: 10px 0; }
    .bar-track { background: var(--mat-sys-surface-container-high); border-radius: 4px; height: 14px; overflow: hidden; }
    .bar { background: var(--mat-sys-primary); height: 100%; border-radius: 4px; }
    .bar.alt { background: var(--mat-sys-tertiary); }
    .bar-value { text-align: right; }
    .columns { display: flex; gap: 12px; align-items: flex-end; height: 200px; padding-top: 12px; }
    .col { flex: 1; display: flex; flex-direction: column; align-items: center; height: 100%; }
    .col-value { font-size: 11px; }
    .col-track { flex: 1; width: 60%; display: flex; align-items: flex-end; }
    .col-bar { width: 100%; background: var(--mat-sys-primary); border-radius: 4px 4px 0 0; min-height: 2px; }
    .upcoming { display: flex; gap: 12px; padding: 8px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .upcoming.overdue .small { color: var(--mat-sys-error); }
    .small { font-size: 12px; }
  `,
})
export class Dashboard {
  readonly meta = inject(MetadataService);
  readonly currency = this.meta.currency;
  readonly icons = ACTIVITY_ICONS;
  readonly overdue = isOverdue;
  readonly mine = signal(false);
  readonly data = httpResource<DashboardSummary>(() => ({ url: '/api/dashboard', params: toParams({ mine: this.mine() }) }));
  readonly summary = stableValue(this.data);

  readonly maxStage = computed(() => Math.max(1, ...(this.summary()?.pipelineByStage.map((s) => s.amount) ?? [])));
  readonly maxMonth = computed(() => Math.max(1, ...(this.summary()?.wonByMonth.map((m) => m.amount) ?? [])));
  readonly maxSource = computed(() => Math.max(1, ...(this.summary()?.leadsBySource.map((s) => s.count) ?? [])));

  /** A section shows when the user can read the object and the tenant has it switched on. */
  has(s: DashboardSummary, objectKey: string): boolean {
    return s.sections.includes(objectKey) && this.meta.isActive(objectKey);
  }

  maxCount(stages: StageSummary[]): number {
    return Math.max(1, ...stages.map((s) => s.count));
  }

  pct(value: number, max: number): number {
    return Math.round((value / max) * 100);
  }

  monthName(month: number): string {
    return MONTHS[month - 1];
  }
}
