import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';

import { AuthService } from './auth.service';
import { ProblemDetails } from './models';

/**
 * Attaches the bearer token and turns the API's problem-details payload into a plain message the
 * UI can show.
 *
 * A 401 no longer means the session is over. Access tokens last an hour, so the common cause is
 * simply that this one aged out — the interceptor spends the refresh token and replays the request
 * once. Only when that fails is the user signed out, which is the difference between an hour-long
 * session and a month-long one.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);

  return next(withToken(req, auth.accessToken())).pipe(
    catchError((error: HttpErrorResponse) => {
      // Auth endpoints answer for themselves: a rejected code or a dead refresh token is the
      // answer, not something to retry with a new token.
      if (error.status !== 401 || req.url.includes('/api/auth/')) {
        return throwError(() => new Error(describe(error)));
      }

      return auth.refresh().pipe(
        switchMap((token) => {
          if (!token) {
            auth.logout();
            return throwError(() => new Error(describe(error)));
          }

          // Once only. A second 401 with a token minted seconds ago is the API saying no, not a
          // timing problem, and retrying again would loop.
          return next(withToken(req, token)).pipe(
            catchError((retried: HttpErrorResponse) => {
              if (retried.status === 401) {
                auth.logout();
              }

              return throwError(() => new Error(describe(retried)));
            }),
          );
        }),
      );
    }),
  );
};

function withToken(req: HttpRequest<unknown>, token: string | null): HttpRequest<unknown> {
  return token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;
}

function describe(error: HttpErrorResponse): string {
  if (error.status === 0) {
    return 'Could not reach the API. Is it running?';
  }

  const problem = error.error as ProblemDetails | undefined;

  if (problem?.detail) {
    return problem.detail;
  }

  if (problem?.title) {
    return problem.title;
  }

  // 402 is the credit-specific case and deserves a clearer message than "Payment Required".
  if (error.status === 402) {
    return 'Insufficient credits for this action.';
  }

  // 429 carries a Retry-After the caller can act on, so say when rather than just "too many".
  if (error.status === 429) {
    const retryAfter = Number(error.headers?.get('Retry-After'));

    return Number.isFinite(retryAfter) && retryAfter > 0
      ? `Too many requests. Try again in ${Math.ceil(retryAfter)} seconds.`
      : 'Too many requests. Try again shortly.';
  }

  return `Request failed (${error.status}).`;
}
