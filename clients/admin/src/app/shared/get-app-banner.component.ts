import { Component, signal } from '@angular/core';

import { environment } from '../../environments/environment';

type Platform = 'android' | 'ios' | null;

const DISMISSED_KEY = 'parknest.getAppDismissed';

/**
 * "Get the app", shown only to someone on a phone browser.
 *
 * Android downloads the APK straight from the GitHub release (the app talks to the same hosted
 * API as this site). iPhones cannot install an app from outside the App Store, so iOS gets the
 * next best thing: adding the site to the Home Screen, where it opens full-screen like an app.
 */
@Component({
  selector: 'app-get-app-banner',
  standalone: true,
  template: `
    @if (platform() && !dismissed()) {
      <aside class="get-app" role="complementary" aria-label="Get the ParkNest app">
        <div class="mark" aria-hidden="true">P</div>
        <div class="copy">
          @if (platform() === 'android') {
            <strong>ParkNest for Android</strong>
            <span>Book and check in faster in the app.</span>
          } @else if (showIosSteps()) {
            <strong>Add ParkNest to your Home Screen</strong>
            <span>Tap <b>Share</b> <span aria-hidden="true">⎋</span> in Safari, then <b>Add to Home Screen</b>.</span>
          } @else {
            <strong>ParkNest on your iPhone</strong>
            <span>The App Store app is coming. Use it like an app today.</span>
          }
        </div>
        @if (platform() === 'android') {
          <a class="btn primary sm" [href]="apkUrl" download="parknest.apk" rel="noopener">Download</a>
        } @else if (!showIosSteps()) {
          <button type="button" class="primary sm" (click)="showIosSteps.set(true)">How</button>
        }
        <button type="button" class="close" aria-label="Dismiss" (click)="dismiss()">×</button>
      </aside>
      @if (platform() === 'android') {
        <p class="hint">After downloading, open the file and allow installs from your browser if Android asks.</p>
      }
    }
  `,
  styles: [
    `
      .get-app {
        display: flex;
        align-items: center;
        gap: 12px;
        padding: 10px 12px 10px 16px;
        background: var(--surface);
        border-bottom: 1px solid var(--border);
      }

      .mark {
        flex: 0 0 auto;
        width: 36px;
        height: 36px;
        border-radius: 10px;
        display: grid;
        place-items: center;
        background: var(--accent);
        color: #fff;
        font: 700 18px/1 var(--font-display);
      }

      .copy {
        flex: 1 1 auto;
        min-width: 0;
        display: flex;
        flex-direction: column;
        gap: 2px;
        font-size: 13px;
        color: var(--ink-soft);
      }

      .copy strong {
        font-size: 14px;
        color: var(--ink);
      }

      .close {
        flex: 0 0 auto;
        width: 32px;
        height: 32px;
        padding: 0;
        border: 0;
        background: transparent;
        color: var(--ink-muted);
        font-size: 22px;
        line-height: 1;
      }

      .hint {
        margin: 0;
        padding: 6px 16px 8px;
        font-size: 12px;
        color: var(--ink-muted);
        background: var(--sunken);
        border-bottom: 1px solid var(--border);
      }
    `,
  ],
})
export class GetAppBannerComponent {
  readonly apkUrl = environment.androidApkUrl;
  readonly platform = signal<Platform>(detectPlatform());
  readonly dismissed = signal(readDismissed());
  readonly showIosSteps = signal(false);

  dismiss(): void {
    this.dismissed.set(true);
    try {
      localStorage.setItem(DISMISSED_KEY, '1');
    } catch {
      // Blocked storage: it stays dismissed for this page view, which is enough.
    }
  }
}

function detectPlatform(): Platform {
  if (typeof navigator === 'undefined') {
    return null;
  }

  // Already opened from the Home Screen or inside the app: nothing to offer.
  const standalone =
    window.matchMedia?.('(display-mode: standalone)').matches ||
    (navigator as Navigator & { standalone?: boolean }).standalone === true;
  if (standalone) {
    return null;
  }

  const ua = navigator.userAgent;
  if (/android/i.test(ua)) {
    return 'android';
  }
  // iPadOS reports itself as a Mac; the touch points give it away.
  if (/iphone|ipad|ipod/i.test(ua) || (/macintosh/i.test(ua) && navigator.maxTouchPoints > 1)) {
    return 'ios';
  }
  return null;
}

function readDismissed(): boolean {
  try {
    return localStorage.getItem(DISMISSED_KEY) === '1';
  } catch {
    return false;
  }
}
