import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { NavigationEnd, Router } from '@angular/router';
import { catchError, filter, of } from 'rxjs';

import { environment } from '../../environments/environment';

const VISITOR_KEY = 'parknest.visitorId';
const SESSION_KEY = 'parknest.sessionId';

/**
 * Tells the API that someone opened the site, and which screens they looked at.
 *
 * Two ids, and the difference between them is the difference between the two numbers on the
 * dashboard. The visitor id lives in local storage and outlives the tab, so a person who comes
 * back tomorrow is the same visitor; the session id lives in session storage and dies with the
 * tab, so tomorrow is a second visit. Both are random and mean nothing on their own — this is not
 * a fingerprint, and someone who clears their storage becomes a new visitor, which is the correct
 * and intended outcome.
 *
 * Every call is fire-and-forget and every failure is swallowed. A counter that could make a page
 * fail to load would be worse than no counter, and the API answers 204 regardless of whether it
 * kept the hit.
 */
@Injectable({ providedIn: 'root' })
export class TelemetryService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private readonly visitorId = read(localStorage, VISITOR_KEY) ?? mint(localStorage, VISITOR_KEY);
  private started = false;

  /**
   * Called once, from the root component. Reports the opening of the site when this is a fresh
   * tab, then one page view per navigation — including the first, which is why a first visit
   * shows both.
   */
  start(): void {
    if (this.started) {
      return;
    }

    this.started = true;

    if (!read(sessionStorage, SESSION_KEY)) {
      mint(sessionStorage, SESSION_KEY);
      this.send('site.visit', location.pathname, document.referrer || undefined);
    }

    this.router.events
      .pipe(filter((event): event is NavigationEnd => event instanceof NavigationEnd))
      .subscribe((event) => this.send('page.view', event.urlAfterRedirects));
  }

  /** A search was run. The step before a booking, and the one the funnel is missing without it. */
  search(): void {
    this.send('search.run');
  }

  private send(name: string, path?: string, referrer?: string): void {
    this.http
      .post(
        `${environment.apiBaseUrl}/api/analytics/collect`,
        {
          name,
          visitorId: this.visitorId,
          sessionId: read(sessionStorage, SESSION_KEY),
          // The API cuts the query string off anyway; doing it here too means a wallet return
          // carrying an order id never leaves the browser in the first place.
          path: (path ?? this.router.url).split('?')[0].split('#')[0],
          referrer,
          source: 'web',
        },
        // The body is deliberately not read: 204, and nothing here acts on the answer.
        { responseType: 'text' },
      )
      .pipe(catchError(() => of(null)))
      .subscribe();
  }
}

/**
 * Storage can throw outright — Safari in private mode, a browser with cookies blocked — and a
 * counter is never worth an exception on the way into the app.
 */
function read(store: Storage, key: string): string | null {
  try {
    return store.getItem(key);
  } catch {
    return null;
  }
}

function mint(store: Storage, key: string): string {
  const id = crypto.randomUUID();

  try {
    store.setItem(key, id);
  } catch {
    // Not stored, so the next page load mints another one. The visit still counts; only the
    // "unique visitors" figure loses a little precision, which is the right thing to lose.
  }

  return id;
}
