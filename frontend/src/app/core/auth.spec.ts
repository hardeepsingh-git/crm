import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService } from './auth.service';
import { authInterceptor } from './http';
import { AuthResponse } from './models';

const response = (token: string, refresh: string): AuthResponse => ({
  accessToken: token,
  refreshToken: refresh,
  expiresAt: new Date(Date.now() + 60_000).toISOString(),
  user: { id: 'u1', email: 'a@b.c', firstName: 'Ada', lastName: 'Lovelace', fullName: 'Ada Lovelace', roles: ['SalesRep'] },
});

describe('AuthService + authInterceptor', () => {
  let auth: AuthService;
  let http: HttpClient;
  let backend: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'login', children: [] }]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  function login(): void {
    auth.login('a@b.c', 'pw').subscribe();
    backend.expectOne('/api/auth/login').flush(response('access-1', 'refresh-1'));
  }

  it('stores the user and refresh token on login', () => {
    login();
    expect(auth.isAuthenticated()).toBe(true);
    expect(auth.user()?.fullName).toBe('Ada Lovelace');
    expect(auth.isManager()).toBe(false);
    expect(localStorage.getItem('crm.refreshToken')).toBe('refresh-1');
  });

  it('adds the bearer token to API calls', () => {
    login();
    http.get('/api/accounts').subscribe();
    const req = backend.expectOne('/api/accounts');
    expect(req.request.headers.get('Authorization')).toBe('Bearer access-1');
    req.flush({});
  });

  it('refreshes once on 401 and retries with the new token', () => {
    login();
    let result: unknown;
    http.get('/api/accounts').subscribe((r) => (result = r));

    backend.expectOne('/api/accounts').flush(null, { status: 401, statusText: 'Unauthorized' });
    const refresh = backend.expectOne('/api/auth/refresh');
    expect(refresh.request.body).toEqual({ refreshToken: 'refresh-1' });
    refresh.flush(response('access-2', 'refresh-2'));

    const retry = backend.expectOne('/api/accounts');
    expect(retry.request.headers.get('Authorization')).toBe('Bearer access-2');
    retry.flush({ ok: true });
    expect(result).toEqual({ ok: true });
    expect(localStorage.getItem('crm.refreshToken')).toBe('refresh-2');
  });

  it('clears the session when refresh fails', () => {
    login();
    http.get('/api/accounts').subscribe({ error: () => undefined });
    backend.expectOne('/api/accounts').flush(null, { status: 401, statusText: 'Unauthorized' });
    backend.expectOne('/api/auth/refresh').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(auth.isAuthenticated()).toBe(false);
    expect(localStorage.getItem('crm.refreshToken')).toBeNull();
  });

  it('does not send tokens to non-API URLs', () => {
    login();
    http.get('/assets/config.json').subscribe();
    const req = backend.expectOne('/assets/config.json');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({});
  });
});
