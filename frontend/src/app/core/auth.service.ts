import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, defer, finalize, firstValueFrom, from, shareReplay, tap, throwError } from 'rxjs';
import { MetadataService } from './metadata.service';
import { ADMIN_RESOURCES, AuthResponse, PermissionScope, UserInfo } from './models';

const REFRESH_KEY = 'crm.refreshToken';
const REFRESH_LOCK = 'crm.auth.refresh';

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
  private readonly metadata = inject(MetadataService);
  private accessToken: string | null = null;
  private refreshInFlight: Observable<AuthResponse> | null = null;

  readonly user = signal<UserInfo | null>(null);
  readonly isAuthenticated = computed(() => this.user() !== null);
  readonly isAdmin = computed(() => this.user()?.isAdministrator ?? false);
  readonly isPlatformAdmin = computed(() => this.user()?.isPlatformAdmin ?? false);
  readonly hasAdminAccess = computed(() => ADMIN_RESOURCES.some((r) => this.can(r.key, r.action)));

  get token(): string | null {
    return this.accessToken;
  }

  /**
   * Effective scope for an action. The server sends resolved scopes for "*" and every resource a role mentions;
   * anything else follows "*" (business objects) or is denied (admin areas).
   */
  scope(resource: string, action: string): PermissionScope {
    const user = this.user();
    if (!user) return 'None';
    if (user.isAdministrator) return 'All';
    const direct = user.permissions[resource]?.[action];
    if (direct !== undefined) return direct;
    if (ADMIN_RESOURCES.some((r) => r.key === resource)) return 'None';
    return user.permissions['*']?.[action] ?? 'None';
  }

  can(resource: string, action: string): boolean {
    return this.scope(resource, action) !== 'None';
  }

  canAccess(resource: string, action: string, ownerId: string): boolean {
    const user = this.user();
    switch (this.scope(resource, action)) {
      case 'All':
        return true;
      case 'Group':
        return !!user && user.teamUserIds.includes(ownerId);
      case 'Own':
        return user?.id === ownerId;
      default:
        return false;
    }
  }

  canModify(resource: string, ownerId: string): boolean {
    return this.canAccess(resource, 'update', ownerId);
  }

  canDelete(resource: string, ownerId: string): boolean {
    return this.canAccess(resource, 'delete', ownerId);
  }

  /** Whether the user may assign owners other than themselves. */
  canAssign(resource: string): boolean {
    const scope = this.scope(resource, 'update');
    return scope === 'All' || scope === 'Group';
  }

  login(email: string, password: string): Observable<AuthResponse> {
    return this.http.post<AuthResponse>('/api/auth/login', { email, password }).pipe(
      tap((r) => {
        this.metadata.clear();
        this.store(r);
      }),
    );
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
    if (!localStorage.getItem(REFRESH_KEY)) return throwError(() => new Error('No refresh token'));
    // Read the token at send time: another tab may have rotated it while this one waited for the lock.
    const send = defer(() =>
      this.http.post<AuthResponse>('/api/auth/refresh', { refreshToken: localStorage.getItem(REFRESH_KEY) }),
    );
    // Tabs share the stored refresh token. Rotating it from two tabs at once would look like token reuse
    // and revoke the session, so rotations are serialized across tabs where the Web Locks API exists.
    const locks = globalThis.navigator?.locks;
    const rotated = locks ? defer(() => from(locks.request(REFRESH_LOCK, () => firstValueFrom(send)))) : send;
    this.refreshInFlight ??= rotated.pipe(
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

  /** Re-reads the current user's permissions, e.g. after an admin changed roles. */
  reloadUser(): void {
    this.http.get<UserInfo>('/api/auth/me').subscribe((u) => this.user.set(u));
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
    this.metadata.clear();
  }
}
