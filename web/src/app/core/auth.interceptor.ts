import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth.service';

const isAuthCall = (req: HttpRequest<unknown>) => req.url.startsWith('/api/v1/auth/');

/**
 * Adds the bearer token to API calls. On a 401 it refreshes once and retries the request;
 * if the refresh also fails, the session is over and the user is sent to the login page.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const withToken = (r: HttpRequest<unknown>) =>
    auth.token && !isAuthCall(r) ? r.clone({ setHeaders: { Authorization: `Bearer ${auth.token}` } }) : r;

  return next(withToken(req)).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401 || isAuthCall(req)) {
        return throwError(() => error);
      }
      return auth.refresh().pipe(
        switchMap(() => next(withToken(req))),
        catchError((refreshError: unknown) => {
          void auth.logout();
          return throwError(() => refreshError);
        }),
      );
    }),
  );
};
