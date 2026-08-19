import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, finalize, map, of, shareReplay, tap } from 'rxjs';

import { environment } from '../../environments/environment';
import { AuthResult, OtpChallenge, UserRole } from './models';

const TOKEN_KEY = 'parknest.token';
const EXPIRY_KEY = 'parknest.expiresAt';
const REFRESH_KEY = 'parknest.refreshToken';
const ROLE_KEY = 'parknest.role';
const USER_KEY = 'parknest.userId';

/**
 * Holds the bearer token and what it says about the caller.
 *
 * The role here drives *what the UI offers*, never what the user is allowed to do — the API
 * re-checks every request. Treating a client-side claim as authorisation would be exactly the
 * defect the backend was just fixed to avoid.
 *
 * Access tokens now last an hour rather than half a day, so the session survives on the refresh
 * token instead. That token rotates on every use: whatever comes back must be stored, because
 * presenting a spent one is read by the API as a leak and ends the session outright.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private readonly token = signal<string | null>(localStorage.getItem(TOKEN_KEY));
  private readonly expiresAt = signal<string | null>(localStorage.getItem(EXPIRY_KEY));
  private readonly refreshToken = signal<string | null>(localStorage.getItem(REFRESH_KEY));

  /**
   * The refresh in flight, if any. Shared so a burst of requests all hitting an expired token
   * produces one refresh rather than several — and several would be worse than wasteful, since
   * each rotation invalidates the last and the losers would look like replays.
   */
  private inFlight: Observable<string | null> | null = null;

  readonly role = signal<UserRole | null>(localStorage.getItem(ROLE_KEY) as UserRole | null);
  readonly userId = signal<string | null>(localStorage.getItem(USER_KEY));

  /** A live session: either the access token is good, or a refresh token can get a new one. */
  readonly isAuthenticated = computed(() => this.hasValidAccessToken() || !!this.refreshToken());

  readonly isAdmin = computed(() => this.role() === 'Admin');

  accessToken(): string | null {
    return this.hasValidAccessToken() ? this.token() : null;
  }

  requestOtp(phone: string): Observable<OtpChallenge> {
    return this.http.post<OtpChallenge>(`${environment.apiBaseUrl}/api/auth/request-otp`, { phone });
  }

  verifyOtp(phone: string, code: string): Observable<AuthResult> {
    return this.http
      .post<AuthResult>(`${environment.apiBaseUrl}/api/auth/verify-otp`, { phone, code })
      .pipe(tap((result) => this.store(result)));
  }

  /**
   * Exchanges the stored refresh token for a fresh pair. Returns the new access token, or null if
   * the session is over. Concurrent callers share one request.
   */
  refresh(): Observable<string | null> {
    if (this.inFlight) {
      return this.inFlight;
    }

    const refreshToken = this.refreshToken();

    if (!refreshToken) {
      return of(null);
    }

    this.inFlight = this.http
      .post<AuthResult>(`${environment.apiBaseUrl}/api/auth/refresh`, { refreshToken })
      .pipe(
        map((result) => {
          // The rotated token *is* the session now; failing to store it would strand the user on
          // the next request and look like a replay to the API.
          this.store(result);
          return result.accessToken as string | null;
        }),
        // A refresh that fails is a session that is over. There is nothing left to retry with.
        catchError(() => {
          this.clear();
          return of(null);
        }),
        finalize(() => (this.inFlight = null)),
        shareReplay({ bufferSize: 1, refCount: false }),
      );

    return this.inFlight;
  }

  /** Ends the session on the server too, so the refresh token cannot outlive the click. */
  logout(): void {
    const refreshToken = this.refreshToken();

    if (refreshToken) {
      // Fire and forget: the local session goes regardless of whether the call lands.
      this.http
        .post(`${environment.apiBaseUrl}/api/auth/logout`, { refreshToken })
        .pipe(catchError(() => of(null)))
        .subscribe();
    }

    this.clear();
    void this.router.navigate(['/login']);
  }

  private hasValidAccessToken(): boolean {
    const expiry = this.expiresAt();

    if (!this.token() || !expiry) {
      return false;
    }

    // A token past its expiry is worthless; checking here avoids a round trip that would only come
    // back 401.
    return new Date(expiry).getTime() > Date.now();
  }

  private clear(): void {
    [TOKEN_KEY, EXPIRY_KEY, REFRESH_KEY, ROLE_KEY, USER_KEY].forEach((key) =>
      localStorage.removeItem(key),
    );

    this.token.set(null);
    this.expiresAt.set(null);
    this.refreshToken.set(null);
    this.role.set(null);
    this.userId.set(null);
  }

  private store(result: AuthResult): void {
    localStorage.setItem(TOKEN_KEY, result.accessToken);
    localStorage.setItem(EXPIRY_KEY, result.expiresAt);
    localStorage.setItem(REFRESH_KEY, result.refreshToken);
    localStorage.setItem(ROLE_KEY, result.role);
    localStorage.setItem(USER_KEY, result.userId);

    this.token.set(result.accessToken);
    this.expiresAt.set(result.expiresAt);
    this.refreshToken.set(result.refreshToken);
    this.role.set(result.role);
    this.userId.set(result.userId);
  }
}
