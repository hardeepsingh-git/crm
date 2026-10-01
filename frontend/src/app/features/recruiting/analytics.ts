import { DecimalPipe, PercentPipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { stableValue, toParams } from '../../core/api';
import { MetadataService } from '../../core/metadata.service';
import { HiringAnalytics, PagedResult, RecordItem } from '../../core/models';

export const PERIODS = [30, 90, 180, 365];

/** Recruitment funnel, time to hire, source quality and per-job pipeline. */
@Component({
  selector: 'app-hiring-analytics',
  imports: [DecimalPipe, PercentPipe, RouterLink, MatCardModule, MatFormFieldModule, MatProgressBarModule, MatSelectModule],
  template: `
    <div class="page-header">
      <div><h1>Hiring analytics</h1><div class="muted">{{ meta.label('application', true) }} created in the period, as far as you can see them.</div></div>
      <span class="spacer"></span>
      <mat-form-field appearance="outline" class="filter">
        <mat-label>{{ meta.label('position') }}</mat-label>
        <mat-select [value]="positionId()" (selectionChange)="positionId.set($event.value)">
          <mat-option [value]="null">All</mat-option>
          @for (j of jobs(); track j.id) { <mat-option [value]="j.id">{{ j.name }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field appearance="outline" class="filter">
        <mat-label>Period</mat-label>
        <mat-select [value]="days()" (selectionChange)="days.set($event.value)">
          @for (p of periods; track p) { <mat-option [value]="p">Last {{ p }} days</mat-option> }
        </mat-select>
      </mat-form-field>
    </div>
    @if (report.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (r(); as r) {
      <section class="kpis">
        <mat-card appearance="outlined" class="kpi"><div class="label">{{ meta.label('application', true) }}</div>
          <div class="value">{{ r.applications }}</div><div class="muted">{{ r.open }} still open</div></mat-card>
        <mat-card appearance="outlined" class="kpi"><div class="label">Hired</div>
          <div class="value">{{ r.hired }}</div><div class="muted">{{ r.rejected }} rejected</div></mat-card>
        <mat-card appearance="outlined" class="kpi"><div class="label">Time to hire</div>
          <div class="value">{{ r.averageDaysToHire === null ? '–' : (r.averageDaysToHire | number: '1.0-1') + ' d' }}</div>
          <div class="muted">average; median {{ r.medianDaysToHire === null ? '–' : (r.medianDaysToHire | number: '1.0-1') + ' d' }}</div></mat-card>
        <mat-card appearance="outlined" class="kpi"><div class="label">Interviews next 7 days</div>
          <div class="value">{{ r.interviewsNext7Days }}</div><div class="muted"><a routerLink="/recruiting/interviews">My interviews</a></div></mat-card>
        <mat-card appearance="outlined" class="kpi" [class.alert]="r.feedbackOutstanding > 0"><div class="label">Feedback outstanding</div>
          <div class="value">{{ r.feedbackOutstanding }}</div><div class="muted">scorecards not yet submitted</div></mat-card>
        <mat-card appearance="outlined" class="kpi"><div class="label">Offer acceptance</div>
          <div class="value">{{ r.offers.acceptanceRate === null ? '–' : (r.offers.acceptanceRate | percent: '1.0-0') }}</div>
          <div class="muted">{{ r.offers.accepted }} accepted, {{ r.offers.declined }} declined, {{ r.offers.expired }} expired</div></mat-card>
        <mat-card appearance="outlined" class="kpi"><div class="label">Offers awaiting reply</div>
          <div class="value">{{ r.offers.pending }}</div>
          <div class="muted">{{ r.offers.averageDaysToRespond === null ? 'of ' + r.offers.sent + ' sent' : 'replies take ' + (r.offers.averageDaysToRespond | number: '1.0-1') + ' days' }}</div></mat-card>
      </section>

      <div class="grid">
        <mat-card appearance="outlined">
          <mat-card-header><mat-card-title>Funnel</mat-card-title><mat-card-subtitle>How far applications got</mat-card-subtitle></mat-card-header>
          <mat-card-content>
            @for (s of openStages(); track s.key) {
              <div class="bar-row">
                <span>{{ s.label }}</span>
                <div class="bar-track"><div class="bar" [style.width.%]="share(s.reached)" [style.background]="s.color"></div></div>
                <span class="bar-value">{{ s.reached }}</span>
              </div>
              <div class="step muted">
                {{ s.current }} here now
                @if (s.averageDaysInStage !== null) { &middot; avg {{ s.averageDaysInStage | number: '1.0-1' }} days in stage }
                @if (s.conversionToNext !== null) { &middot; <strong>{{ s.conversionToNext | percent: '1.0-0' }}</strong> moved on }
              </div>
            }
            <div class="closed">
              @for (s of closedStages(); track s.key) { <span><span class="dot" [style.background]="s.color"></span>{{ s.label }}: {{ s.current }}</span> }
            </div>
          </mat-card-content>
        </mat-card>

        <mat-card appearance="outlined">
          <mat-card-header><mat-card-title>Sources</mat-card-title><mat-card-subtitle>Where applicants come from and who gets hired</mat-card-subtitle></mat-card-header>
          <mat-card-content>
            <table class="table">
              <thead><tr><th>Source</th><th class="num">Applications</th><th class="num">Hired</th><th class="num">Hire rate</th></tr></thead>
              <tbody>
                @for (s of r.sources; track s.source) {
                  <tr><td>{{ s.source }}</td><td class="num">{{ s.applications }}</td><td class="num">{{ s.hired }}</td><td class="num">{{ s.hireRate | percent: '1.0-0' }}</td></tr>
                } @empty { <tr><td colspan="4" class="muted">No applications in this period.</td></tr> }
              </tbody>
            </table>
          </mat-card-content>
        </mat-card>
      </div>

      <mat-card appearance="outlined" class="jobs">
        <mat-card-header><mat-card-title>{{ meta.label('position', true) }}</mat-card-title></mat-card-header>
        <mat-card-content>
          <div class="table-scroll">
            <table class="table">
              <thead><tr>
                <th>{{ meta.label('position') }}</th><th class="num">Total</th><th class="num">Open</th>
                @for (s of r.funnel; track s.key) { <th class="num">{{ s.label }}</th> }
              </tr></thead>
              <tbody>
                @for (j of r.jobs; track j.id) {
                  <tr>
                    <td><a [routerLink]="['/o/position', j.id]">{{ j.title }}</a></td><td class="num">{{ j.applications }}</td><td class="num">{{ j.open }}</td>
                    @for (s of r.funnel; track s.key) { <td class="num">{{ j.byStage[s.key] ?? 0 }}</td> }
                  </tr>
                } @empty { <tr><td [attr.colspan]="3 + r.funnel.length" class="muted">No applications in this period.</td></tr> }
              </tbody>
            </table>
          </div>
        </mat-card-content>
      </mat-card>
    }
  `,
  styles: `
    .filter { width: 200px; margin-left: 8px; }
    .kpis { display: grid; grid-template-columns: repeat(auto-fill, minmax(190px, 1fr)); gap: 16px; margin-bottom: 16px; }
    .kpi { padding: 16px; }
    .kpi .label { font: var(--mat-sys-label-large); color: var(--mat-sys-on-surface-variant); }
    .kpi .value { font: var(--mat-sys-headline-medium); margin: 4px 0; }
    .kpi.alert .value { color: var(--mat-sys-error); }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(420px, 1fr)); gap: 16px; margin-bottom: 16px; }
    .bar-row { display: grid; grid-template-columns: 120px 1fr 48px; gap: 8px; align-items: center; margin-top: 10px; }
    .bar-track { background: var(--mat-sys-surface-container-high); border-radius: 4px; height: 16px; overflow: hidden; }
    .bar { height: 100%; border-radius: 4px; min-width: 2px; }
    .bar-value { text-align: right; font-weight: 500; }
    .step { font-size: 12px; margin-left: 128px; }
    .closed { display: flex; gap: 16px; flex-wrap: wrap; margin-top: 16px; font-size: 14px; }
    .dot { display: inline-block; width: 10px; height: 10px; border-radius: 50%; margin-right: 6px; }
    .table { width: 100%; border-collapse: collapse; font-size: 14px; }
    .table th, .table td { padding: 8px; border-bottom: 1px solid var(--mat-sys-outline-variant); text-align: left; white-space: nowrap; }
    .table th { font-weight: 500; color: var(--mat-sys-on-surface-variant); }
    .table .num { text-align: right; }
    .table-scroll { overflow-x: auto; }
  `,
})
export class HiringAnalyticsPage {
  readonly meta = inject(MetadataService);
  readonly periods = PERIODS;
  readonly days = signal(90);
  readonly positionId = signal<string | null>(null);

  readonly report = httpResource<HiringAnalytics>(() => ({
    url: '/api/recruiting/analytics',
    params: toParams({ days: this.days(), positionId: this.positionId() }),
  }));
  readonly r = stableValue(this.report);
  private readonly jobList = httpResource<PagedResult<RecordItem>>(() => ({
    url: '/api/records/position',
    params: toParams({ pageSize: 100, sortBy: 'name', sortDir: 'asc' }),
  }));
  readonly jobs = computed(() => (this.jobList.hasValue() ? this.jobList.value().items : []));

  readonly openStages = computed(() => {
    const keys = new Set(this.meta.openStages('application').map((s) => s.key));
    return (this.r()?.funnel ?? []).filter((s) => keys.has(s.key));
  });
  readonly closedStages = computed(() => {
    const open = new Set(this.openStages().map((s) => s.key));
    return (this.r()?.funnel ?? []).filter((s) => !open.has(s.key));
  });
  private readonly top = computed(() => Math.max(1, ...this.openStages().map((s) => s.reached)));

  share(n: number): number {
    return (n / this.top()) * 100;
  }
}
