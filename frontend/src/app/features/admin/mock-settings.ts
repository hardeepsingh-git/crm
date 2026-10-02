import { httpResource } from '@angular/common/http';
import { Component, inject, linkedSignal, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatRadioModule } from '@angular/material/radio';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { RouterLink } from '@angular/router';
import { CrmApi } from '../../core/api';
import { AiReviewTrigger, MockSettings } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SettingsBack } from './settings-home';

interface Form {
  aiReviewEnabled: boolean;
  aiTrigger: AiReviewTrigger;
  aiInstructions: string;
}

/** Settings > Mock interviews: the question library and the organisation's AI feedback opt-in. */
@Component({
  selector: 'app-mock-settings',
  imports: [FormsModule, RouterLink, MatButtonModule, MatCardModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MatRadioModule, MatSlideToggleModule, SettingsBack],
  template: `
    <div class="page-header"><div><app-settings-back /><h1>Mock interviews</h1></div></div>
    @if (settings.isLoading() && !settings.hasValue()) { <mat-progress-bar mode="indeterminate" /> }
    @if (settings.hasValue()) {
      @let s = settings.value();
      <div class="grid">
        <mat-card appearance="outlined">
          <mat-card-header><mat-icon mat-card-avatar>quiz</mat-icon><mat-card-title>Question library</mat-card-title></mat-card-header>
          <mat-card-content>
            @if (s.libraryAvailable) {
              <p class="muted">Your organisation's practice questions. Link a question to a job opening to use it only for that job, add a model answer
                that candidates see after answering, and retire questions you no longer use.</p>
              <div class="actions">
                <a mat-flat-button routerLink="/o/mock_question"><mat-icon>list</mat-icon> Open the library</a>
                <button mat-stroked-button (click)="addStarter()" [disabled]="busy()"><mat-icon>auto_awesome</mat-icon> Add starter questions</button>
              </div>
            } @else {
              <p class="muted">The question library comes with the Recruitment template. Ask a platform administrator to upgrade your organisation.</p>
            }
          </mat-card-content>
        </mat-card>

        <mat-card appearance="outlined">
          <mat-card-header><mat-icon mat-card-avatar class="ai">auto_awesome</mat-icon><mat-card-title>AI feedback</mat-card-title></mat-card-header>
          <mat-card-content>
            <p class="muted">A language model scores each answer and suggests strengths and areas to work on. It only receives the job title, the questions,
              model answers and the candidate's answers; never their name or contact details. Results are visible to the hiring team only.</p>
            <div class="connection" [attr.data-ready]="s.aiConnectionReady">
              <mat-icon>{{ s.aiConnectionReady ? 'cloud_done' : 'cloud_off' }}</mat-icon>
              <span>{{ s.aiConnectionReady ? 'An AI provider is connected for this platform.' : s.aiConnectionProblem }}</span>
            </div>
            <mat-slide-toggle [ngModel]="form().aiReviewEnabled" (ngModelChange)="patch({ aiReviewEnabled: $event })">Use AI feedback in our organisation</mat-slide-toggle>
            @if (form().aiReviewEnabled) {
              <div class="trigger">
                <mat-radio-group [ngModel]="form().aiTrigger" (ngModelChange)="patch({ aiTrigger: $event })" aria-label="When to run">
                  <mat-radio-button value="Manual">When someone asks for it on a completed mock interview</mat-radio-button>
                  <mat-radio-button value="OnSubmit">Automatically when the candidate submits</mat-radio-button>
                </mat-radio-group>
              </div>
              <mat-form-field appearance="outline" class="full">
                <mat-label>Grading guidance (optional)</mat-label>
                <textarea matInput rows="4" [ngModel]="form().aiInstructions" (ngModelChange)="patch({ aiInstructions: $event })" maxlength="4000"
                  placeholder="e.g. We value structured answers with measurable results. Be encouraging."></textarea>
                <mat-hint>Added to the model's instructions. It can't change how results are reported.</mat-hint>
              </mat-form-field>
            }
            <div class="actions"><button mat-flat-button (click)="save()" [disabled]="busy()">Save</button></div>
          </mat-card-content>
        </mat-card>
      </div>
    }
  `,
  styles: `
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(340px, 1fr)); gap: 16px; align-items: start; }
    mat-icon[mat-card-avatar] { color: var(--mat-sys-primary); font-size: 32px; width: 40px; height: 40px; }
    mat-icon.ai { color: var(--mat-sys-tertiary); }
    .actions { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 16px; }
    .connection { display: flex; gap: 8px; align-items: center; padding: 10px 12px; border-radius: 12px; margin: 8px 0 16px;
      background: var(--crm-warning-container); color: var(--crm-on-warning-container); }
    .connection[data-ready='true'] { background: var(--crm-success-container); color: var(--crm-on-success-container); }
    .trigger mat-radio-group { display: flex; flex-direction: column; margin: 12px 0; }
    .full { width: 100%; }
  `,
})
export class MockInterviewSettings {
  private readonly api = inject(CrmApi);
  private readonly notify = inject(NotifyService);
  readonly busy = signal(false);
  readonly settings = httpResource<MockSettings>(() => '/api/mock-interviews/settings');
  /** Editable copy, reset whenever the saved settings (re)load. */
  readonly form = linkedSignal<Form>(() => {
    const s = this.settings.hasValue() ? this.settings.value() : null;
    return { aiReviewEnabled: s?.aiReviewEnabled ?? false, aiTrigger: s?.aiTrigger ?? 'Manual', aiInstructions: s?.aiInstructions ?? '' };
  });

  patch(change: Partial<Form>): void {
    this.form.update((f) => ({ ...f, ...change }));
  }

  save(): void {
    const f = this.form();
    this.busy.set(true);
    this.api.saveMockSettings({ aiReviewEnabled: f.aiReviewEnabled, aiTrigger: f.aiTrigger, aiInstructions: f.aiInstructions.trim() || null }).subscribe({
      next: (s) => {
        this.busy.set(false);
        this.settings.set(s);
        this.notify.success('Mock interview settings saved');
      },
      error: (err) => {
        this.busy.set(false);
        this.notify.formError(err);
      },
    });
  }

  addStarter(): void {
    this.busy.set(true);
    this.api.addStarterQuestions().subscribe({
      next: (r) => {
        this.busy.set(false);
        this.notify.success(r.added ? `${r.added} starter questions added` : 'Your library already has all the starter questions');
      },
      error: () => this.busy.set(false),
    });
  }
}
