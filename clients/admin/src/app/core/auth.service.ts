import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, tap } from 'rxjs';

import { environment } from '../../environments/environment';
import { AuthResult, OtpChallenge, UserRole } from './models';

const TOKEN_KEY = 'parknest.token';
const EXPIRY_KEY = 'parknest.expiresAt';
const ROLE_KEY = 'parknest.role';
const USER_KEY = 'parknest.userId';

/**
 * Holds the bearer token and what it says about the caller.
 *
 * The role here drives *what the UI offers*, never what the user is allowed to do — the API
 * re-checks every request. Treating a client-side claim as authorisation would be exactly the
 * defect the backend was just fixed to avoid.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private readonly token = signal<string | null>(localStorage.getItem(TOKEN_KEY));
  private readonly expiresAt = signal<string | null>(localStorage.getItem(EXPIRY_KEY));

  readonly role = signal<UserRole | null>(localStorage.getItem(ROLE_KEY) as UserRole | null);
  readonly userId = signal<string | null>(localStorage.getItem(USER_KEY));

  readonly isAuthenticated = computed(() => {
    const expiry = this.expiresAt();
    if (!this.token() || !expiry) {
      return false;
    }

    // A token past its expiry is worthless; checking here avoids a pointless round trip that
    // would only come back 401.
    return new Date(expiry).getTime() > Date.now();
  });

  readonly isAdmin = computed(() => this.role() === 'Admin');

  accessToken(): string | null {
    return this.isAuthenticated() ? this.token() : null;
  }

  requestOtp(phone: string): Observable<OtpChallenge> {
    return this.http.post<OtpChallenge>(`${environment.apiBaseUrl}/api/auth/request-otp`, { phone });
  }

  verifyOtp(phone: string, code: string): Observable<AuthResult> {
    return this.http
      .post<AuthResult>(`${environment.apiBaseUrl}/api/auth/verify-otp`, { phone, code })
      .pipe(tap((result) => this.store(result)));
  }

  logout(): void {
    [TOKEN_KEY, EXPIRY_KEY, ROLE_KEY, USER_KEY].forEach((key) => localStorage.removeItem(key));

    this.token.set(null);
    this.expiresAt.set(null);
    this.role.set(null);
    this.userId.set(null);

    void this.router.navigate(['/login']);
  }

  private store(result: AuthResult): void {
    localStorage.setItem(TOKEN_KEY, result.accessToken);
    localStorage.setItem(EXPIRY_KEY, result.expiresAt);
    localStorage.setItem(ROLE_KEY, result.role);
    localStorage.setItem(USER_KEY, result.userId);

    this.token.set(result.accessToken);
    this.expiresAt.set(result.expiresAt);
    this.role.set(result.role);
    this.userId.set(result.userId);
  }
}
