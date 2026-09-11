import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';

import { environment } from '../../environments/environment';
import { AuthService } from './auth.service';
import { AuthResult } from './models';

/**
 * The session, which is the one piece of client state that can strand a user.
 *
 * Access tokens last an hour and the session lives on a refresh token that rotates on every use,
 * so most of what is worth asserting here is about that rotation: storing what comes back, not
 * sending two refreshes for one expiry, and giving up cleanly when the session really is over.
 */
describe('AuthService', () => {
  const base = environment.apiBaseUrl;

  let auth: AuthService;
  let http: HttpTestingController;
  let router: jasmine.SpyObj<Router>;

  /** An hour out, which is what the API issues. */
  function result(overrides: Partial<AuthResult> = {}): AuthResult {
    return {
      accessToken: 'access-1',
      refreshToken: 'refresh-1',
      expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
      refreshExpiresAt: new Date(Date.now() + 30 * 86_400_000).toISOString(),
      role: 'Admin',
      userId: 'user-1',
      isNewUser: false,
      ...overrides,
    };
  }

  beforeEach(() => {
    localStorage.clear();
    router = jasmine.createSpyObj<Router>('Router', ['navigate']);

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: router },
      ],
    });

    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    localStorage.clear();
  });

  function signIn(overrides: Partial<AuthResult> = {}): AuthResult {
    const payload = result(overrides);
    auth.verifyOtp('9876543210', '123456').subscribe();
    http.expectOne(`${base}/api/auth/verify-otp`).flush(payload);
    return payload;
  }

  it('has no session before anybody signs in', () => {
    expect(auth.isAuthenticated()).toBeFalse();
    expect(auth.accessToken()).toBeNull();
    expect(auth.isAdmin()).toBeFalse();
  });

  it('stores the whole result on sign-in, not just the token', () => {
    const payload = signIn();

    expect(auth.accessToken()).toBe(payload.accessToken);
    expect(auth.isAuthenticated()).toBeTrue();
    expect(auth.role()).toBe('Admin');
    expect(auth.userId()).toBe('user-1');

    // Survives a reload. A session held only in memory logs the operator out on every refresh.
    expect(localStorage.getItem('parknest.refreshToken')).toBe(payload.refreshToken);
  });

  it('treats an expired access token as absent rather than sending it', () => {
    signIn({ expiresAt: new Date(Date.now() - 1_000).toISOString() });

    // Checked locally so a request that is certain to come back 401 is never made.
    expect(auth.accessToken()).toBeNull();
  });

  it('is still authenticated on an expired access token, because the refresh token can renew it', () => {
    signIn({ expiresAt: new Date(Date.now() - 1_000).toISOString() });

    // The distinction the whole refresh-token design exists for: an hour-old access token is not
    // a finished session. Reporting otherwise would bounce the user to login every hour.
    expect(auth.accessToken()).toBeNull();
    expect(auth.isAuthenticated()).toBeTrue();
  });

  it('stores the rotated token that comes back from a refresh', () => {
    signIn();

    let issued: string | null = null;
    auth.refresh().subscribe((token) => (issued = token));

    const rotated = result({ accessToken: 'access-2', refreshToken: 'refresh-2' });
    http.expectOne(`${base}/api/auth/refresh`).flush(rotated);

    expect(issued!).toBe('access-2');

    // The rotated refresh token *is* the session now. Keeping the old one would present a spent
    // token on the next refresh, which the API reads as a leak and answers by ending the session.
    expect(localStorage.getItem('parknest.refreshToken')).toBe('refresh-2');
  });

  it('sends one refresh for a burst of callers, not one each', () => {
    signIn();

    const seen: (string | null)[] = [];
    auth.refresh().subscribe((t) => seen.push(t));
    auth.refresh().subscribe((t) => seen.push(t));
    auth.refresh().subscribe((t) => seen.push(t));

    // Three refreshes would rotate three times, and the two losers would look like replays — so
    // a burst of expired requests would end the very session it was trying to renew.
    http.expectOne(`${base}/api/auth/refresh`).flush(result({ accessToken: 'access-2' }));

    expect(seen).toEqual(['access-2', 'access-2', 'access-2']);
  });

  it('allows a fresh refresh once the in-flight one has settled', () => {
    signIn();

    auth.refresh().subscribe();
    http.expectOne(`${base}/api/auth/refresh`).flush(result({ accessToken: 'access-2' }));

    auth.refresh().subscribe();
    http.expectOne(`${base}/api/auth/refresh`).flush(result({ accessToken: 'access-3' }));

    expect(auth.accessToken()).toBe('access-3');
  });

  it('ends the session when the refresh token is refused', () => {
    signIn();

    let issued: string | null = 'unset';
    auth.refresh().subscribe((token) => (issued = token));

    http.expectOne(`${base}/api/auth/refresh`).flush(null, { status: 401, statusText: 'Unauthorized' });

    // Null rather than an error: a dead refresh token is an answer, and there is nothing left to
    // retry with.
    expect(issued!).toBeNull();
    expect(auth.isAuthenticated()).toBeFalse();
    expect(localStorage.getItem('parknest.refreshToken')).toBeNull();
  });

  it('refreshes to null without a request when there is no refresh token', () => {
    let issued: string | null = 'unset';
    auth.refresh().subscribe((token) => (issued = token));

    expect(issued!).toBeNull();
    http.expectNone(`${base}/api/auth/refresh`);
  });

  it('revokes server-side on logout rather than only forgetting locally', () => {
    const payload = signIn();

    auth.logout();

    const revoke = http.expectOne(`${base}/api/auth/logout`);
    expect(revoke.request.body).toEqual({ refreshToken: payload.refreshToken });
    revoke.flush(null);

    expect(auth.isAuthenticated()).toBeFalse();
    expect(router.navigate).toHaveBeenCalledWith(['/login']);
  });

  it('signs out locally even when the revoke call fails', () => {
    signIn();

    auth.logout();
    http.expectOne(`${base}/api/auth/logout`).flush(null, { status: 500, statusText: 'Server Error' });

    // The click has to mean something on this machine regardless of what the server says.
    expect(auth.isAuthenticated()).toBeFalse();
    expect(localStorage.getItem('parknest.token')).toBeNull();
  });

  it('reports admin only for the admin role', () => {
    signIn({ role: 'Both' });

    // The role drives what the console offers, never what it permits — the API re-checks the
    // admin role on every one of these endpoints regardless of what gets rendered.
    expect(auth.isAdmin()).toBeFalse();
    expect(auth.isAuthenticated()).toBeTrue();
  });
});
