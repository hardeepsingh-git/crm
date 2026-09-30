import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';

interface ProblemDetails {
  title?: string;
  detail?: string;
  errors?: Record<string, string[]>;
}

@Injectable({ providedIn: 'root' })
export class NotifyService {
  private readonly snackBar = inject(MatSnackBar);

  success(message: string): void {
    this.snackBar.open(message, 'OK', { duration: 3000 });
  }

  warn(message: string): void {
    this.snackBar.open(message, 'Dismiss', { duration: 6000, panelClass: 'snack-error' });
  }

  error(err: unknown): void {
    this.snackBar.open(errorMessage(err), 'Dismiss', { duration: 8000, panelClass: 'snack-error' });
  }

  /** For form submits: other statuses are already reported by the global error interceptor. */
  formError(err: unknown): void {
    if (err instanceof HttpErrorResponse && err.status === 400) this.error(err);
  }
}

export function errorMessage(err: unknown): string {
  if (!(err instanceof HttpErrorResponse)) return 'Something went wrong.';
  if (err.status === 0) return 'Cannot reach the server. Check your connection.';
  if (err.status === 403) return 'You do not have permission to do that.';
  if (err.status === 404) return 'The record was not found. It may have been deleted.';
  if (err.status === 429) return 'Too many attempts. Please wait a minute and try again.';
  if (typeof err.error === 'string' && err.error) return err.error;

  const problem = err.error as ProblemDetails | null;
  if (problem?.errors) return Object.values(problem.errors).flat().join(' ');
  return problem?.detail ?? problem?.title ?? `Request failed (${err.status}).`;
}
