import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthService, isTokenRejected } from './auth.service';
import { MetadataService } from './metadata.service';
import { NotifyService } from './notify.service';

const ANONYMOUS = ['/api/auth/login', '/api/auth/refresh', '/api/auth/logout'];

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith('/api/')) return next(req);
  const auth = inject(AuthService);
  const isAnonymous = ANONYMOUS.some((u) => req.url.startsWith(u));
  const withToken = (r: HttpRequest<unknown>) =>
    auth.token ? r.clone({ setHeaders: { Authorization: `Bearer ${auth.token}` } }) : r;

  return next(withToken(req)).pipe(
    catchError((err: unknown) => {
      if (!(err instanceof HttpErrorResponse) || err.status !== 401 || isAnonymous) return throwError(() => err);
      return auth.refresh().pipe(
        catchError((refreshErr: unknown) => {
          if (isTokenRejected(refreshErr)) auth.expire();
          return throwError(() => err);
        }),
        switchMap(() => next(withToken(req))),
      );
    }),
  );
};

/** Surfaces unexpected failures globally. 400 (validation), 401 (auth), auth and public (careers, offer) endpoints are handled by callers. */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const notify = inject(NotifyService);
  const quiet = ['/api/auth/', '/api/careers/', '/api/offer-response/', '/api/mock-response/'].some((prefix) => req.url.startsWith(prefix));
  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status !== 400 && err.status !== 401 && !quiet) notify.error(err);
      return throwError(() => err);
    }),
  );
};

export const authGuard: CanActivateFn = async (_, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  // A stored session may not have been restored at startup (e.g. a transient network error).
  if (!auth.isAuthenticated()) await auth.restore();
  return auth.isAuthenticated() || router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};

export const permissionGuard =
  (resource: string, action: string): CanActivateFn =>
  () =>
    inject(AuthService).can(resource, action) || inject(Router).createUrlTree(['/']);

export const adminAreaGuard: CanActivateFn = () => inject(AuthService).hasAdminAccess() || inject(Router).createUrlTree(['/']);

export const platformGuard: CanActivateFn = () => inject(AuthService).isPlatformAdmin() || inject(Router).createUrlTree(['/']);

/** Loads the tenant's metadata (objects, fields, stages) before any page that renders records. */
export const metadataGuard: CanActivateFn = async () => {
  const metadata = inject(MetadataService);
  if (!metadata.data()) await metadata.load();
  return true;
};

/** Record pages: the object must be switched on for the tenant and readable by the user. Runs after metadataGuard. */
export const objectGuard =
  (objectKey?: string): CanActivateFn =>
  (route) => {
    const key = objectKey ?? route.paramMap.get('objectKey') ?? '';
    return (inject(MetadataService).isActive(key) && inject(AuthService).can(key, 'read')) || inject(Router).createUrlTree(['/']);
  };
