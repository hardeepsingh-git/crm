import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, finalize, firstValueFrom, shareReplay, tap, throwError } from 'rxjs';
import { AuthResponse, Role, UserInfo } from './models';

const REFRESH_KEY = 'crm.refreshToken';

/** True only when the server says the refresh token is invalid, as opposed to a transient failure. */
export function isTokenRejected(err: unknown): boolean {
  return err instanceof HttpErrorResponse && err.status === 401;
}

/**
 * Access token lives only in memory; the refresh token is persisted so a page reload can restore the session.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private accessToken: string | null = null;
  private refreshInFlight: Observable<AuthResponse> | null = null;

  readonly user = signal<UserInfo | null>(null);
  readonly isAuthenticated = computed(() => this.user() !== null);
  readonly isAdmin = computed(() => this.hasRole('Admin'));
  readonly isManager = computed(() => this.hasRole('Admin', 'Manager'));

  get token(): string | null {
    return this.accessToken;
  }

  hasRole(...roles: Role[]): boolean {
    const mine = this.user()?.roles ?? [];
    return roles.some((r) => mine.includes(r));
  }

  canModify(ownerId: string): boolean {
    return this.isManager() || this.user()?.id === ownerId;
  }

  login(email: string, password: string): Observable<AuthResponse> {
    return this.http.post<AuthResponse>('/api/auth/login', { email, password }).pipe(tap((r) => this.store(r)));
  }

  async restore(): Promise<void> {
    if (!localStorage.getItem(REFRESH_KEY)) return;
    try {
      await firstValueFrom(this.refresh());
    } catch {
      // refresh() already cleared the session if the token was rejected; otherwise keep it for the next attempt.
    }
  }

  refresh(): Observable<AuthResponse> {
    const refreshToken = localStorage.getItem(REFRESH_KEY);
    if (!refreshToken) return throwError(() => new Error('No refresh token'));
    this.refreshInFlight ??= this.http.post<AuthResponse>('/api/auth/refresh', { refreshToken }).pipe(
      tap({ next: (r) => this.store(r), error: (err: unknown) => isTokenRejected(err) && this.clear() }),
      finalize(() => (this.refreshInFlight = null)),
      shareReplay(1),
    );
    return this.refreshInFlight;
  }

  changePassword(currentPassword: string, newPassword: string): Observable<void> {
    return this.http.post<void>('/api/auth/change-password', { currentPassword, newPassword });
  }

  logout(): void {
    const refreshToken = localStorage.getItem(REFRESH_KEY);
    if (refreshToken) this.http.post('/api/auth/logout', { refreshToken }).subscribe({ error: () => undefined });
    this.clear();
    void this.router.navigateByUrl('/login');
  }

  /** Session could not be renewed: drop state and send the user to login, preserving where they were. */
  expire(): void {
    this.clear();
    void this.router.navigate(['/login'], { queryParams: { returnUrl: this.router.url } });
  }

  private store(r: AuthResponse): void {
    this.accessToken = r.accessToken;
    localStorage.setItem(REFRESH_KEY, r.refreshToken);
    this.user.set(r.user);
  }

  private clear(): void {
    this.accessToken = null;
    localStorage.removeItem(REFRESH_KEY);
    this.user.set(null);
  }
}
