import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';

import { environment } from '../../environments/environment';
import { authInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';

/**
 * The interceptor is the only thing standing between an hour-long access token and an hour-long
 * session. Most of what matters here is what it does with a 401: refresh once, replay once, and
 * know the difference between a token that aged out and an API that is saying no.
 */
describe('authInterceptor', () => {
  const base = environment.apiBaseUrl;
  const url = `${base}/api/wallets/me`;

  let http: HttpClient;
  let backend: HttpTestingController;
  let auth: {
    accessToken: jasmine.Spy<() => string | null>;
    refresh: jasmine.Spy<() => Observable<string | null>>;
    logout: jasmine.Spy<() => void>;
  };

  beforeEach(() => {
    auth = {
      accessToken: jasmine.createSpy('accessToken').and.returnValue('access-1'),
      refresh: jasmine.createSpy('refresh').and.returnValue(of('access-2')),
      logout: jasmine.createSpy('logout'),
    };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: auth },
      ],
    });

    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  /** Collects whatever the caller ends up seeing, success or failure. */
  function call(target = url) {
    const seen: { value?: unknown; error?: Error } = {};
    http.get(target).subscribe({
      next: (value) => (seen.value = value),
      error: (error: Error) => (seen.error = error),
    });
    return seen;
  }

  it('attaches the bearer token', () => {
    call();

    const request = backend.expectOne(url);
    expect(request.request.headers.get('Authorization')).toBe('Bearer access-1');
    request.flush({});
  });

  it('sends no Authorization header when there is no token', () => {
    auth.accessToken.and.returnValue(null);
    call();

    const request = backend.expectOne(url);
    // An empty bearer is worse than none: it is a header the API has to reject rather than a
    // request it can answer anonymously.
    expect(request.request.headers.has('Authorization')).toBeFalse();
    request.flush({});
  });

  it('refreshes and replays once on a 401', () => {
    const seen = call();

    backend.expectOne(url).flush(null, { status: 401, statusText: 'Unauthorized' });

    const replay = backend.expectOne(url);
    // Replayed with the *new* token. Replaying with the old one would 401 again and sign the
    // user out over a token that had simply aged out.
    expect(replay.request.headers.get('Authorization')).toBe('Bearer access-2');
    replay.flush({ balance: 42 });

    expect(seen.value).toEqual({ balance: 42 });
    expect(auth.logout).not.toHaveBeenCalled();
  });

  it('signs out when the replay is refused too', () => {
    const seen = call();

    backend.expectOne(url).flush(null, { status: 401, statusText: 'Unauthorized' });
    backend.expectOne(url).flush(null, { status: 401, statusText: 'Unauthorized' });

    // A second 401 with a token minted seconds ago is the API saying no, not a timing problem.
    // Retrying again would loop.
    expect(auth.logout).toHaveBeenCalledTimes(1);
    expect(seen.error).toBeDefined();
  });

  it('signs out when there is no session left to refresh with', () => {
    auth.refresh.and.returnValue(of(null));
    const seen = call();

    backend.expectOne(url).flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(auth.logout).toHaveBeenCalledTimes(1);
    expect(seen.error).toBeDefined();
    // Nothing is replayed — there is no token to replay with.
    backend.expectNone(url);
  });

  it('does not try to refresh a 401 from an auth endpoint', () => {
    const target = `${base}/api/auth/verify-otp`;
    const seen = call(target);

    backend.expectOne(target).flush(
      { status: 401, title: 'Unauthorized', detail: 'That code is not right.' },
      { status: 401, statusText: 'Unauthorized' },
    );

    // A rejected sign-in code is the answer, not a stale token. Refreshing here would swallow the
    // real message and hand the user "Request failed (401)".
    expect(auth.refresh).not.toHaveBeenCalled();
    expect(seen.error?.message).toBe('That code is not right.');
  });

  it('leaves non-401 failures alone', () => {
    const seen = call();

    backend.expectOne(url).flush(
      { status: 400, title: 'Bad Request', detail: 'Minimum booking duration is 30 minutes.' },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(auth.refresh).not.toHaveBeenCalled();
    expect(seen.error?.message).toBe('Minimum booking duration is 30 minutes.');
  });

  describe('the message the user is shown', () => {
    function messageFor(status: number, body: object | null, headers?: Record<string, string>): string {
      const seen = call();
      backend.expectOne(url).flush(body, { status, statusText: 'x', headers });
      return seen.error!.message;
    }

    it('prefers the API problem detail over anything invented here', () => {
      expect(messageFor(400, { status: 400, title: 'Bad Request', detail: 'Price is outside the band.' }))
        .toBe('Price is outside the band.');
    });

    it('falls back to the problem title when there is no detail', () => {
      expect(messageFor(400, { status: 400, title: 'Bad Request', detail: null })).toBe('Bad Request');
    });

    it('says the API is unreachable rather than reporting a status of zero', () => {
      // Status 0 is a network failure, and "Request failed (0)" tells the operator nothing they
      // can act on. "Is it running?" is the actual next step.
      expect(messageFor(0, null)).toBe('Could not reach the API. Is it running?');
    });

    it('translates 402 into the credit shortfall it always means', () => {
      expect(messageFor(402, null)).toBe('Insufficient credits for this action.');
    });

    it('turns a Retry-After into a number of seconds to wait', () => {
      // The header carries something actionable; "too many requests" alone does not say whether
      // to try again now or in a minute.
      expect(messageFor(429, null, { 'Retry-After': '45' }))
        .toBe('Too many requests. Try again in 45 seconds.');
    });

    it('still handles a rate limit with no Retry-After', () => {
      expect(messageFor(429, null)).toBe('Too many requests. Try again shortly.');
    });

    it('falls back to the status code when the response says nothing useful', () => {
      expect(messageFor(500, null)).toBe('Request failed (500).');
    });
  });

  it('propagates a refresh that itself errors rather than hanging the request', () => {
    auth.refresh.and.returnValue(throwError(() => new Error('refresh exploded')));
    const seen = call();

    backend.expectOne(url).flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(seen.error).toBeDefined();
  });
});
