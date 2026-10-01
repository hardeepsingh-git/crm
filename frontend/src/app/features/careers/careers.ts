import { DatePipe } from '@angular/common';
import { HttpErrorResponse, httpResource } from '@angular/common/http';
import { Component, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { CrmApi } from '../../core/api';
import { CareerJob, CareerJobDetail, CareersPage } from '../../core/models';

export const CV_TYPES = '.pdf,.doc,.docx,.odt,.rtf';
export const MAX_CV_MB = 5;

/** "Engineering · Sydney · Full-time" */
export function jobFacts(job: CareerJob): string {
  return [job.department, job.location, job.employmentType].filter(Boolean).join(' · ');
}

/** Field errors from a 400 ProblemDetails, as display messages. */
export function problemMessages(err: unknown): string[] {
  if (!(err instanceof HttpErrorResponse)) return ['Something went wrong. Please try again.'];
  if (err.status === 404) return ['Sorry, this job is no longer open.'];
  if (err.status === 429) return ['Too many applications from your network. Please wait a minute and try again.'];
  const errors = (err.error as { errors?: Record<string, string[]> } | null)?.errors;
  return errors ? Object.values(errors).flat() : ['Something went wrong. Please try again.'];
}

@Component({
  selector: 'app-careers-page',
  imports: [DatePipe, RouterLink, MatCardModule, MatIconModule, MatProgressBarModule],
  template: `
    <main class="careers">
      @if (page.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      @if (page.error()) {
        <h1>Careers</h1>
        <p class="muted">This careers page isn't available.</p>
      }
      @if (page.hasValue()) {
        @let p = page.value();
        <header>
          <h1>Careers at {{ p.organisation }}</h1>
          @if (p.intro) { <p class="intro">{{ p.intro }}</p> }
        </header>
        <h2>Open positions ({{ p.jobs.length }})</h2>
        @for (job of p.jobs; track job.id) {
          <a class="job" [routerLink]="[job.id]">
            <mat-card appearance="outlined">
              <mat-card-content>
                <h3>{{ job.title }}</h3>
                <div class="muted">{{ facts(job) }}</div>
                @if (job.closingDate) { <div class="muted small">Applications close {{ job.closingDate | date: 'd MMMM y' }}</div> }
              </mat-card-content>
            </mat-card>
          </a>
        } @empty {
          <p class="muted">There are no open positions right now. Please check back soon.</p>
        }
      }
    </main>
  `,
  styleUrl: './careers.css',
})
export class CareersPageView {
  readonly slug = input.required<string>();
  readonly page = httpResource<CareersPage>(() => `/api/careers/${encodeURIComponent(this.slug())}`);
  readonly facts = jobFacts;
}

@Component({
  selector: 'app-career-job',
  imports: [
    DatePipe, ReactiveFormsModule, RouterLink, MatButtonModule, MatCardModule, MatCheckboxModule, MatFormFieldModule, MatIconModule,
    MatInputModule, MatProgressBarModule,
  ],
  template: `
    <main class="careers">
      <a [routerLink]="['/careers', slug()]" class="back"><mat-icon inline>arrow_back</mat-icon> All positions</a>
      @if (detail.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      @if (detail.error()) { <p class="muted">Sorry, this job is no longer open.</p> }
      @if (detail.hasValue()) {
        @let d = detail.value();
        <header>
          <div class="muted">{{ d.organisation }}</div>
          <h1>{{ d.job.title }}</h1>
          <div class="muted">{{ facts(d.job) }}</div>
          @if (d.job.closingDate) { <div class="muted small">Applications close {{ d.job.closingDate | date: 'd MMMM y' }}</div> }
        </header>
        @if (d.description) { <p class="description">{{ d.description }}</p> }

        <mat-card appearance="outlined" class="apply">
          <mat-card-content>
            @if (submitted()) {
              <h2><mat-icon inline>check_circle</mat-icon> Application sent</h2>
              <p>{{ submitted() }}</p>
            } @else {
              <h2>Apply for this job</h2>
              <form [formGroup]="form" (ngSubmit)="submit()" class="form">
                <mat-form-field appearance="outline">
                  <mat-label>Full name</mat-label>
                  <input matInput formControlName="name" autocomplete="name" maxlength="200" required />
                </mat-form-field>
                <mat-form-field appearance="outline">
                  <mat-label>Email</mat-label>
                  <input matInput type="email" formControlName="email" autocomplete="email" maxlength="256" required />
                  @if (form.controls.email.hasError('email')) { <mat-error>Enter a valid email address</mat-error> }
                </mat-form-field>
                <div class="row">
                  <mat-form-field appearance="outline">
                    <mat-label>Phone</mat-label>
                    <input matInput type="tel" formControlName="phone" autocomplete="tel" maxlength="50" />
                  </mat-form-field>
                  <mat-form-field appearance="outline">
                    <mat-label>Location</mat-label>
                    <input matInput formControlName="location" autocomplete="address-level2" maxlength="255" />
                  </mat-form-field>
                </div>
                <mat-form-field appearance="outline">
                  <mat-label>LinkedIn profile</mat-label>
                  <input matInput type="url" formControlName="linkedin" placeholder="https://www.linkedin.com/in/…" maxlength="500" />
                </mat-form-field>
                <mat-form-field appearance="outline">
                  <mat-label>Cover note</mat-label>
                  <textarea matInput formControlName="coverNote" rows="5" maxlength="4000"></textarea>
                </mat-form-field>
                <!-- Honeypot: invisible to people, so anything typed here comes from a bot. -->
                <div class="hp" aria-hidden="true">
                  <label>Website <input type="text" formControlName="website" tabindex="-1" autocomplete="off" /></label>
                </div>
                <div class="cv">
                  <button mat-stroked-button type="button" (click)="file.click()"><mat-icon>attach_file</mat-icon> Attach CV</button>
                  <input #file type="file" [accept]="cvTypes" hidden (change)="pick(file.files)" />
                  <span class="muted">{{ cv()?.name ?? 'PDF or Word, up to ' + maxCvMb + ' MB' }}</span>
                </div>
                <mat-checkbox formControlName="consent">
                  I agree that {{ d.organisation }} may store and use my details to assess this application and contact me about it.
                </mat-checkbox>
                @if (errors().length) {
                  <ul class="errors" role="alert">@for (e of errors(); track e) { <li>{{ e }}</li> }</ul>
                }
                @if (sending()) { <mat-progress-bar mode="indeterminate" /> }
                <div><button mat-flat-button type="submit" [disabled]="sending()">Send application</button></div>
              </form>
            }
          </mat-card-content>
        </mat-card>
      }
    </main>
  `,
  styleUrl: './careers.css',
})
export class CareerJobView {
  readonly slug = input.required<string>();
  readonly jobId = input.required<string>();
  private readonly api = inject(CrmApi);
  readonly detail = httpResource<CareerJobDetail>(() => `/api/careers/${encodeURIComponent(this.slug())}/jobs/${this.jobId()}`);
  readonly facts = jobFacts;
  readonly cvTypes = CV_TYPES;
  readonly maxCvMb = MAX_CV_MB;
  readonly cv = signal<File | null>(null);
  readonly errors = signal<string[]>([]);
  readonly sending = signal(false);
  readonly submitted = signal<string | null>(null);

  readonly form = inject(FormBuilder).nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
    email: ['', [Validators.required, Validators.email]],
    phone: [''],
    location: [''],
    linkedin: [''],
    coverNote: [''],
    website: [''],
    consent: [false],
  });

  pick(files: FileList | null): void {
    const file = files?.[0] ?? null;
    this.errors.set(file && file.size > MAX_CV_MB * 1024 * 1024 ? [`Your CV can be at most ${MAX_CV_MB} MB.`] : []);
    this.cv.set(this.errors().length ? null : file);
  }

  /** Checks what the browser can, so the common mistakes don't need a round trip; the server checks everything again. */
  validate(): string[] {
    const v = this.form.getRawValue();
    const problems: string[] = [];
    if (this.form.controls.name.invalid) problems.push('Enter your full name.');
    if (this.form.controls.email.invalid) problems.push('Enter a valid email address.');
    if (v.linkedin.trim() && !/^https?:\/\//i.test(v.linkedin.trim())) problems.push('Enter your LinkedIn profile as a full link, starting with https://.');
    if (!this.cv()) problems.push('Attach your CV.');
    if (!v.consent) problems.push('Please agree to the privacy notice to apply.');
    return problems;
  }

  submit(): void {
    this.form.markAllAsTouched();
    const problems = this.validate();
    this.errors.set(problems);
    if (problems.length) return;

    const v = this.form.getRawValue();
    const data = new FormData();
    for (const key of ['name', 'email', 'phone', 'location', 'linkedin', 'coverNote', 'website'] as const) data.append(key, v[key].trim());
    data.append('consent', String(v.consent));
    data.append('cv', this.cv()!, this.cv()!.name);
    this.sending.set(true);
    this.api.applyForJob(this.slug(), this.jobId(), data).subscribe({
      next: (r) => {
        this.sending.set(false);
        this.submitted.set(r.message);
      },
      error: (err) => {
        this.sending.set(false);
        this.errors.set(problemMessages(err));
      },
    });
  }
}
