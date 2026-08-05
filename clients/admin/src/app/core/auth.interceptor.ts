import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';

import { AuthService } from './auth.service';
import { ProblemDetails } from './models';

/**
 * Attaches the bearer token and turns the API's problem-details payload into a plain message the
 * UI can show. A 401 clears the session — the token is either expired or was rejected, and either
 * way holding onto it just produces another 401 on the next click.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const token = auth.accessToken();

  const authorised = token
    ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
    : req;

  return next(authorised).pipe(
    catchError((error: HttpErrorResponse) => {
      if (error.status === 401 && !req.url.includes('/api/auth/')) {
        auth.logout();
      }

      return throwError(() => new Error(describe(error)));
    }),
  );
};

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

  return `Request failed (${error.status}).`;
}
