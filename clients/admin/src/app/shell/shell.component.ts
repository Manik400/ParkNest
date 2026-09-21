import { Component, ElementRef, HostListener, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { AuthService } from '../core/auth.service';

/**
 * Top bar on desktop, bottom tab bar on a phone. The renter's three actions (explore, bookings,
 * wallet) are always one tap away; hosting and the admin screens live behind the avatar so the
 * bar never grows past what a thumb can reach.
 */
@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header class="top">
      <a routerLink="/" class="brand" aria-label="ParkNest home">
        <span class="mark"><span></span></span>
        <span class="wordmark">ParkNest</span>
      </a>

      <nav class="links">
        <a routerLink="/explore" routerLinkActive="active">Find parking</a>
        <a routerLink="/bookings" routerLinkActive="active">Bookings</a>
        <a routerLink="/wallet" routerLinkActive="active">Wallet</a>
      </nav>

      <div class="right">
        <a routerLink="/listings/new" class="host-link">List your space</a>

        <div class="menu-anchor">
          <button
            type="button"
            class="avatar-btn"
            [attr.aria-expanded]="menuOpen()"
            aria-haspopup="menu"
            (click)="menuOpen.set(!menuOpen())"
          >
            <span class="lines" aria-hidden="true"></span>
            <span class="avatar">{{ initial() }}</span>
          </button>

          @if (menuOpen()) {
            <div class="menu" role="menu" (click)="menuOpen.set(false)">
              <a routerLink="/profile" role="menuitem">Profile</a>
              <a routerLink="/vehicles" role="menuitem">My vehicles</a>
              <a routerLink="/disputes" role="menuitem">{{ auth.isAdmin() ? 'Disputes' : 'My disputes' }}</a>
              <div class="sep"></div>
              <div class="menu-label">Hosting</div>
              <a routerLink="/listings" role="menuitem">Your spaces</a>
              <a routerLink="/listings/new" role="menuitem">List a new space</a>
              @if (auth.isAdmin()) {
                <div class="sep"></div>
                <div class="menu-label">Operations</div>
                <a routerLink="/analytics" role="menuitem">Site activity</a>
                <a routerLink="/kyc" role="menuitem">Identity checks</a>
                <a routerLink="/payouts" role="menuitem">Payouts</a>
                <a routerLink="/pricing" role="menuitem">Pricing bands</a>
              }
              <div class="sep"></div>
              <button type="button" class="menu-btn" role="menuitem" (click)="auth.logout()">Sign out</button>
            </div>
          }
        </div>
      </div>
    </header>

    <main>
      <router-outlet />
    </main>

    <nav class="tabs" aria-label="Primary">
      <a routerLink="/explore" routerLinkActive="active"><i class="ic ic-explore"></i>Explore</a>
      <a routerLink="/bookings" routerLinkActive="active"><i class="ic ic-bookings"></i>Bookings</a>
      <a routerLink="/listings" routerLinkActive="active"><i class="ic ic-host"></i>Host</a>
      <a routerLink="/wallet" routerLinkActive="active"><i class="ic ic-wallet"></i>Wallet</a>
      <a routerLink="/profile" routerLinkActive="active"><i class="ic ic-profile"></i>Profile</a>
    </nav>
  `,
  styles: [
    `
      :host {
        display: block;
        min-height: 100vh;
      }

      .top {
        position: sticky;
        top: 0;
        z-index: 20;
        height: 72px;
        display: flex;
        align-items: center;
        gap: 24px;
        padding: 0 clamp(16px, 3vw, 32px);
        background: var(--canvas);
        border-bottom: 1px solid var(--hairline);
      }

      .brand {
        display: flex;
        align-items: center;
        gap: 9px;
        color: var(--ink);
        text-decoration: none;
      }

      .mark {
        width: 26px;
        height: 26px;
        border-radius: 8px;
        background: var(--accent);
        display: flex;
        align-items: center;
        justify-content: center;
      }

      .mark span {
        width: 9px;
        height: 9px;
        border-radius: 3px;
        background: var(--canvas);
      }

      .wordmark {
        font: 700 18px/1 var(--font-display);
        letter-spacing: -0.02em;
      }

      .links {
        display: flex;
        gap: 4px;
        margin-left: 8px;
      }

      .links a {
        padding: 10px 14px;
        border-radius: 999px;
        color: var(--ink-soft);
        font: 600 14px/1 var(--font-body);
        text-decoration: none;
      }

      .links a:hover {
        background: var(--sunken);
        color: var(--ink);
      }

      .links a.active {
        color: var(--ink);
        background: var(--sunken);
      }

      .right {
        margin-left: auto;
        display: flex;
        align-items: center;
        gap: 14px;
      }

      .host-link {
        color: var(--ink);
        font: 600 14px/1 var(--font-body);
        padding: 10px 12px;
        border-radius: 999px;
      }

      .host-link:hover {
        background: var(--sunken);
        text-decoration: none;
      }

      .menu-anchor {
        position: relative;
      }

      .avatar-btn {
        height: 42px;
        padding: 0 6px 0 14px;
        gap: 8px;
        border: 1px solid var(--border);
        border-radius: 999px;
        background: var(--surface);
      }

      .lines {
        width: 16px;
        height: 2px;
        background: var(--ink-soft);
        box-shadow: 0 5px 0 var(--ink-soft), 0 -5px 0 var(--ink-soft);
      }

      .avatar {
        width: 30px;
        height: 30px;
        border-radius: 50%;
        background: var(--accent-300);
        color: #7a3a18;
        display: flex;
        align-items: center;
        justify-content: center;
        font: 700 13px/1 var(--font-body);
      }

      .menu {
        position: absolute;
        right: 0;
        top: calc(100% + 8px);
        width: 240px;
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: 16px;
        box-shadow: var(--shadow-lg);
        padding: 8px;
        display: flex;
        flex-direction: column;
      }

      .menu a,
      .menu-btn {
        display: block;
        width: 100%;
        height: auto;
        padding: 10px 12px;
        border: 0;
        border-radius: 10px;
        background: transparent;
        color: var(--ink);
        font: 600 14px/1.2 var(--font-body);
        text-align: left;
        text-decoration: none;
        justify-content: flex-start;
      }

      .menu a:hover,
      .menu-btn:hover:not(:disabled) {
        background: var(--sunken);
        border: 0;
      }

      .menu-label {
        padding: 10px 12px 4px;
        font: 800 10px/1 var(--font-body);
        letter-spacing: 0.1em;
        text-transform: uppercase;
        color: var(--ink-muted);
      }

      .sep {
        height: 1px;
        background: var(--hairline);
        margin: 6px 4px;
      }

      main {
        max-width: 1280px;
        margin: 0 auto;
        padding: 28px clamp(16px, 3vw, 32px) 64px;
      }

      /* Mobile tab bar: hidden on desktop, fixed at the bottom on a phone. */
      .tabs {
        display: none;
      }

      @media (max-width: 720px) {
        .top {
          height: 60px;
        }

        .links,
        .host-link {
          display: none;
        }

        main {
          padding: 20px 16px calc(84px + env(safe-area-inset-bottom));
        }

        .tabs {
          position: fixed;
          bottom: 0;
          left: 0;
          right: 0;
          z-index: 20;
          display: grid;
          grid-template-columns: repeat(5, 1fr);
          padding: 8px 4px calc(10px + env(safe-area-inset-bottom));
          background: var(--surface);
          border-top: 1px solid var(--hairline);
        }

        .tabs a {
          display: flex;
          flex-direction: column;
          align-items: center;
          gap: 5px;
          color: var(--ink-muted);
          font: 600 10px/1 var(--font-body);
          text-decoration: none;
        }

        .tabs a.active {
          color: var(--accent);
          font-weight: 700;
        }

        .ic {
          width: 22px;
          height: 22px;
          border-radius: 7px;
          background: var(--border-strong);
        }

        .tabs a.active .ic {
          background: var(--accent);
        }
      }
    `,
  ],
})
export class ShellComponent {
  readonly auth = inject(AuthService);
  private readonly host = inject(ElementRef<HTMLElement>);

  readonly menuOpen = signal(false);

  /** One letter for the avatar; the profile name is not loaded here so the role stands in. */
  readonly initial = () => (this.auth.role() ?? 'P').charAt(0).toUpperCase();

  @HostListener('document:click', ['$event'])
  closeOnOutsideClick(event: MouseEvent): void {
    if (this.menuOpen() && !this.host.nativeElement.querySelector('.menu-anchor')?.contains(event.target as Node)) {
      this.menuOpen.set(false);
    }
  }

  @HostListener('document:keydown.escape')
  closeOnEscape(): void {
    this.menuOpen.set(false);
  }
}
