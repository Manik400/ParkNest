import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';

import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { BookingSummary, ListingSummary, Reconciliation, Wallet } from '../../core/models';
import { StatusPillComponent } from '../../shared/status-pill.component';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CurrencyPipe, DatePipe, RouterLink, StatusPillComponent],
  template: `
    <div class="stack">
      <h1>Dashboard</h1>

      @if (error(); as message) {
        <div class="banner banner--error" role="alert">{{ message }}</div>
      }

      @if (loading()) {
        <p class="muted">Loading…</p>
      } @else {
        <section class="tiles">
          <div class="card tile">
            <span class="muted">Spendable</span>
            <strong>{{ wallet()?.spendable ?? 0 | currency: 'INR' : 'symbol' : '1.2-2' }}</strong>
          </div>
          <div class="card tile">
            <span class="muted">Held against bookings</span>
            <strong>{{ wallet()?.held ?? 0 | currency: 'INR' : 'symbol' : '1.2-2' }}</strong>
          </div>
          <div class="card tile">
            <span class="muted">Earnings awaiting cash-out</span>
            <strong>{{ wallet()?.earning ?? 0 | currency: 'INR' : 'symbol' : '1.2-2' }}</strong>
          </div>
        </section>

        <!-- Divergence between the stored balances and a replay of the ledger is an incident,
             not a cosmetic issue, so it gets surfaced on the landing screen. -->
        @if (reconciliation(); as recon) {
          @if (!recon.matches) {
            <div class="banner banner--error" role="alert">
              <strong>Ledger mismatch.</strong> The stored balances do not agree with a replay of
              this wallet's entries. Investigate before trusting any figure on this page.
            </div>
          }
        }

        <section class="card stack">
          <div class="row space-between">
            <h2>Recent bookings</h2>
            <a routerLink="/bookings">View all</a>
          </div>

          @if (bookings().length === 0) {
            <p class="muted">No bookings yet.</p>
          } @else {
            <div class="table-scroll">
              <table>
                <thead>
                  <tr>
                    <th>Space</th>
                    <th>Starts</th>
                    <th class="numeric">Held</th>
                    <th>Status</th>
                  </tr>
                </thead>
                <tbody>
                  @for (booking of bookings(); track booking.id) {
                    <tr>
                      <td><a [routerLink]="['/bookings', booking.id]">{{ booking.spaceTitle }}</a></td>
                      <td>{{ booking.startTime | date: 'short' }}</td>
                      <td class="numeric">
                        {{ booking.holdAmount | currency: 'INR' : 'symbol' : '1.2-2' }}
                      </td>
                      <td><app-status-pill [status]="booking.status" /></td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>

        <section class="card stack">
          <div class="row space-between">
            <h2>Your listings</h2>
            <a routerLink="/listings">View all</a>
          </div>

          @if (listings().length === 0) {
            <p class="muted">You are not hosting any spaces.</p>
          } @else {
            <ul class="plain">
              @for (listing of listings(); track listing.id) {
                <li>
                  <span>{{ listing.title }} — {{ listing.city }}</span>
                  <app-status-pill [status]="listing.status" />
                </li>
              }
            </ul>
          }
        </section>
      }
    </div>
  `,
  styles: [
    `
      .tiles {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
        gap: var(--space-4);
      }

      .tile {
        display: flex;
        flex-direction: column;
        gap: var(--space-1);
      }

      .tile strong {
        font-size: 1.5rem;
        font-variant-numeric: tabular-nums;
      }

      .space-between {
        justify-content: space-between;
      }

      ul.plain {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: var(--space-2);
      }

      ul.plain li {
        display: flex;
        justify-content: space-between;
        gap: var(--space-3);
        align-items: center;
      }
    `,
  ],
})
export class DashboardComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);

  readonly wallet = signal<Wallet | null>(null);
  readonly reconciliation = signal<Reconciliation | null>(null);
  readonly bookings = signal<BookingSummary[]>([]);
  readonly listings = signal<ListingSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    const userId = this.auth.userId();

    forkJoin({
      wallet: this.api.myWallet(),
      bookings: this.api.myBookings(),
      listings: this.api.myListings(),
      reconciliation: this.api.reconcile(userId!),
    }).subscribe({
      next: (result) => {
        this.wallet.set(result.wallet);
        this.bookings.set(result.bookings.slice(0, 5));
        this.listings.set(result.listings.slice(0, 5));
        this.reconciliation.set(result.reconciliation);
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }
}
