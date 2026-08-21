import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { AuthService } from '../core/auth.service';

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <div class="layout">
      <aside>
        <div class="brand">ParkNest</div>

        <nav>
          <a routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">
            Dashboard
          </a>
          <a routerLink="/explore" routerLinkActive="active">Find parking</a>
          <a routerLink="/vehicles" routerLinkActive="active">My vehicles</a>
          <a routerLink="/listings" routerLinkActive="active">My listings</a>
          <a routerLink="/bookings" routerLinkActive="active">Bookings</a>
          <a routerLink="/wallet" routerLinkActive="active">Wallet</a>
          @if (auth.isAdmin()) {
            <a routerLink="/pricing" routerLinkActive="active">Pricing bands</a>
          }
        </nav>

        <div class="footer">
          <div class="muted role">{{ auth.role() }}</div>
          <button type="button" (click)="auth.logout()">Sign out</button>
        </div>
      </aside>

      <main>
        <router-outlet />
      </main>
    </div>
  `,
  styles: [
    `
      .layout {
        display: grid;
        grid-template-columns: 220px 1fr;
        min-height: 100vh;
      }

      aside {
        display: flex;
        flex-direction: column;
        gap: var(--space-5);
        padding: var(--space-5) var(--space-4);
        background: var(--surface);
        border-right: 1px solid var(--border);
      }

      .brand {
        font-weight: 700;
        font-size: 1.1rem;
      }

      nav {
        display: flex;
        flex-direction: column;
        gap: var(--space-1);
      }

      nav a {
        padding: var(--space-2) var(--space-3);
        border-radius: var(--radius);
        color: var(--text);
        font-size: 0.92rem;
      }

      nav a:hover {
        background: var(--bg);
        text-decoration: none;
      }

      nav a.active {
        background: var(--bg);
        color: var(--accent);
        font-weight: 600;
      }

      .footer {
        margin-top: auto;
        display: flex;
        flex-direction: column;
        gap: var(--space-2);
      }

      .role {
        font-size: 0.8rem;
      }

      main {
        padding: var(--space-6);
        max-width: 1100px;
      }

      /* Stack the nav above the content on narrow screens rather than squeezing a sidebar. */
      @media (max-width: 720px) {
        .layout {
          grid-template-columns: 1fr;
        }

        aside {
          border-right: none;
          border-bottom: 1px solid var(--border);
        }

        nav {
          flex-direction: row;
          flex-wrap: wrap;
        }

        main {
          padding: var(--space-4);
        }
      }
    `,
  ],
})
export class ShellComponent {
  readonly auth = inject(AuthService);
}
