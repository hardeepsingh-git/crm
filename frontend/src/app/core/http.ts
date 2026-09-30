import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth.service';
import { Role } from './models';
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
        catchError(() => {
          auth.expire();
          return throwError(() => err);
        }),
        switchMap(() => next(withToken(req))),
      );
    }),
  );
};

/** Surfaces unexpected failures globally. 400 (validation) and 401 (auth) are handled by callers. */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const notify = inject(NotifyService);
  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status !== 400 && err.status !== 401) notify.error(err);
      return throwError(() => err);
    }),
  );
};

export const authGuard: CanActivateFn = (_, state) =>
  inject(AuthService).isAuthenticated() ||
  inject(Router).createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });

export const roleGuard =
  (...roles: Role[]): CanActivateFn =>
  () =>
    inject(AuthService).hasRole(...roles) || inject(Router).createUrlTree(['/']);
