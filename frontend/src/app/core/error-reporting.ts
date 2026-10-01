import { HttpBackend, HttpClient, HttpErrorResponse } from '@angular/common/http';
import { ErrorHandler, Injectable, inject } from '@angular/core';
import { AuthService } from './auth.service';

const MAX_REPORTS_PER_PAGE = 10;

/** Logs to the console as usual and forwards unexpected browser errors to the API's error log. */
@Injectable()
export class ReportingErrorHandler extends ErrorHandler {
  // Bypasses interceptors: a failed report must not trigger snackbars, token refreshes or another report.
  private readonly http = new HttpClient(inject(HttpBackend));
  private readonly auth = inject(AuthService);
  private readonly reported = new Set<string>();

  override handleError(error: unknown): void {
    super.handleError(error);
    // Failed requests are already logged by the API (or are expected, e.g. validation).
    const original = (error as { rejection?: unknown })?.rejection ?? error;
    if (original instanceof HttpErrorResponse || !this.auth.token) return;

    const message = (original instanceof Error ? original.message : String(original)).slice(0, 1000) || 'Unknown error';
    if (this.reported.has(message) || this.reported.size >= MAX_REPORTS_PER_PAGE) return;
    this.reported.add(message);

    const stack = original instanceof Error ? original.stack?.slice(0, 4000) : undefined;
    this.http
      .post('/api/client-errors', { message, stack, url: location.pathname.slice(0, 500) }, {
        headers: { Authorization: `Bearer ${this.auth.token}` },
      })
      .subscribe({ error: () => undefined });
  }
}
