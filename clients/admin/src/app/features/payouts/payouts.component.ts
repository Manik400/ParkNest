import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { ApiService } from '../../core/api.service';
import { Payout, PayoutStatus } from '../../core/models';
import { StatusPillComponent } from '../../shared/status-pill.component';

/**
 * The cash-out queue.
 *
 * Nothing here moves money by itself — the transfer happens at a bank, and this is where an
 * operator records what happened to it. Recording is not bookkeeping after the fact, though: the
 * credits were debited when the host requested the payout, so marking one failed is what returns
 * them, and leaving a rejected transfer unrecorded strands the host's earnings indefinitely.
 */
@Component({
  selector: 'app-payouts',
  standalone: true,
  imports: [CurrencyPipe, DatePipe, FormsModule, StatusPillComponent],
  template: `
    <div class="stack">
      <div>
        <h1>Payouts</h1>
        <p class="muted">
          Host cash-out requests, oldest first. The credits left the host's balance when they
          asked — recording the outcome here is what settles or returns them.
        </p>
      </div>

      @if (error(); as message) {
        <div class="banner banner--error" role="alert">{{ message }}</div>
      }

      @if (notice(); as message) {
        <div class="banner banner--info" role="status">{{ message }}</div>
      }

      <section class="card stack">
        <div class="row">
          <div>
            <label for="status">Status</label>
            <select id="status" name="status" [(ngModel)]="status" (ngModelChange)="load()">
              <option [ngValue]="'Requested'">Awaiting transfer</option>
              <option [ngValue]="'Paid'">Paid</option>
              <option [ngValue]="'Failed'">Failed</option>
              <option [ngValue]="null">All</option>
            </select>
          </div>

          <button type="button" (click)="load()" [disabled]="loading()">Refresh</button>
        </div>

        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (payouts().length === 0) {
          <p class="muted">Nothing waiting.</p>
        } @else {
          <div class="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>Requested</th>
                  <th>Host</th>
                  <th class="numeric">Amount</th>
                  <th>Status</th>
                  <th>Reference</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (payout of payouts(); track payout.payoutId) {
                  <tr>
                    <td>{{ payout.createdAt | date: 'd MMM, HH:mm' }}</td>
                    <td>
                      {{ payout.hostName || 'Unnamed' }}
                      <div class="muted small">{{ payout.hostPhone }}</div>
                    </td>
                    <td class="numeric">
                      {{ payout.amount | currency: 'INR' : 'symbol' : '1.2-2' }}
                    </td>
                    <td><app-status-pill [status]="payout.status" /></td>
                    <td>
                      {{ payout.providerReference || payout.failureReason || '—' }}
                    </td>
                    <td>
                      @if (payout.status === 'Requested' || payout.status === 'Processing') {
                        <button type="button" (click)="select(payout)" [disabled]="busy()">
                          Record
                        </button>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>

      @if (selected(); as payout) {
        <section class="card stack">
          <h2>Record outcome</h2>
          <p class="muted">
            {{ payout.amount | currency: 'INR' : 'symbol' : '1.2-2' }} to
            {{ payout.hostName || payout.hostPhone }}.
          </p>

          <div class="grid">
            <div>
              <label for="reference">Bank or aggregator reference</label>
              <input id="reference" name="reference" [(ngModel)]="reference" placeholder="UTR…" />
              <small class="muted">Recorded against the payout so it can be traced later.</small>
            </div>

            <div>
              <label for="reason">Reason, if it failed</label>
              <input
                id="reason"
                name="reason"
                [(ngModel)]="reason"
                placeholder="Bank account closed"
              />
              <small class="muted">The host is told this, so make it something they can act on.</small>
            </div>
          </div>

          <div class="actions">
            <button class="primary" type="button" (click)="complete()" [disabled]="busy()">
              Mark paid
            </button>
            <button type="button" (click)="fail()" [disabled]="busy() || !reason.trim()">
              Mark failed and return the credits
            </button>
            <button type="button" (click)="selected.set(null)" [disabled]="busy()">Cancel</button>
          </div>
        </section>
      }
    </div>
  `,
  styles: [
    `
      .row {
        display: flex;
        align-items: flex-end;
        justify-content: space-between;
        gap: var(--space-3);
        flex-wrap: wrap;
      }

      .small {
        font-size: 0.8rem;
      }

      .actions {
        display: flex;
        gap: var(--space-2);
        flex-wrap: wrap;
      }
    `,
  ],
})
export class PayoutsComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly payouts = signal<Payout[]>([]);
  readonly selected = signal<Payout | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);

  status: PayoutStatus | null = 'Requested';
  reference = '';
  reason = '';

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.payouts(this.status ?? undefined).subscribe({
      next: (payouts) => {
        this.payouts.set(payouts);
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }

  select(payout: Payout): void {
    this.selected.set(payout);
    this.reference = '';
    this.reason = '';
    this.notice.set(null);
  }

  complete(): void {
    const payout = this.selected();
    if (!payout) {
      return;
    }

    this.run(
      this.api.completePayout(payout.payoutId, this.reference.trim() || null),
      'Recorded as paid.',
    );
  }

  fail(): void {
    const payout = this.selected();
    if (!payout) {
      return;
    }

    this.run(
      this.api.failPayout(payout.payoutId, this.reason.trim()),
      "Recorded as failed. The credits are back in the host's balance.",
    );
  }

  private run(source: ReturnType<ApiService['completePayout']>, message: string): void {
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);

    source.subscribe({
      next: () => {
        this.busy.set(false);
        this.notice.set(message);
        this.selected.set(null);
        this.load();
      },
      error: (err: Error) => {
        this.busy.set(false);
        this.error.set(err.message);
      },
    });
  }
}
