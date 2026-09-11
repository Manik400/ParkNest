import { CurrencyPipe, DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { forkJoin, of, timer } from 'rxjs';
import { catchError, switchMap, take, takeWhile } from 'rxjs/operators';

import { environment } from '../../../environments/environment';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import {
  CheckoutPayload,
  LedgerEntrySummary,
  PaymentOrderView,
  Reconciliation,
  StartPaymentResult,
  Wallet,
} from '../../core/models';

@Component({
  selector: 'app-wallet',
  standalone: true,
  imports: [CurrencyPipe, DatePipe, FormsModule],
  template: `
    <div class="stack">
      <h1>Wallet</h1>

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
            <span class="muted small">Free to book with</span>
          </div>
          <div class="card tile">
            <span class="muted">Held</span>
            <strong>{{ wallet()?.held ?? 0 | currency: 'INR' : 'symbol' : '1.2-2' }}</strong>
            <span class="muted small">Reserved against open bookings</span>
          </div>
          <div class="card tile">
            <span class="muted">Earning</span>
            <strong>{{ wallet()?.earning ?? 0 | currency: 'INR' : 'symbol' : '1.2-2' }}</strong>
            <span class="muted small">Awaiting cash-out</span>
          </div>
        </section>

        <section class="card stack">
          <div>
            <h2>Add credits</h2>
            <p class="muted">
              Credits are bought through the payment gateway. Nothing is added to your balance
              until the gateway confirms the payment with a signed callback - which is why a
              failed or abandoned payment simply leaves your balance untouched.
            </p>
          </div>

          @if (settling()) {
            <div class="banner banner--info" role="status">
              Confirming the payment with the server… The balance moves only once the gateway's
              signed callback has been verified.
            </div>
          }

          @if (outcome(); as order) {
            @if (order.status === 'Paid') {
              <div class="banner banner--ok" role="status">
                ✓ Paid. {{ order.amount | currency: 'INR' : 'symbol' : '1.2-2' }} added to your
                spendable balance.
              </div>
            } @else if (order.status === 'Failed') {
              <div class="banner banner--error" role="alert">
                Payment failed - nothing was charged and nothing was credited.
                {{ order.failureReason }}
              </div>
            } @else {
              <div class="banner banner--info" role="status">
                Order {{ order.providerOrderId }} is still open. If you completed the payment, the
                callback has not arrived yet; the credits will appear when it does.
              </div>
            }
          }

          <form class="row" (ngSubmit)="startPayment()">
            <div class="amount">
              <label for="amount">Amount</label>
              <input id="amount" name="amount" type="number" min="100" step="100" [(ngModel)]="topUpAmount" />
            </div>
            <button class="primary" type="submit" [disabled]="paying() || settling()">
              {{ paying() ? 'Starting…' : 'Add credits' }}
            </button>
          </form>

          @if (paymentError(); as message) {
            <div class="banner banner--error" role="alert">{{ message }}</div>
          }

          @if (pendingOrder(); as order) {
            <!-- Every gateway the API offers returns a checkout URL, so this is a fallback for a
                 payload without one: show the order rather than nothing. -->
            <div class="banner banner--info">
              Order <strong>{{ order.providerOrderId }}</strong> created for
              {{ order.amount | currency: 'INR' : 'symbol' : '1.2-2' }}. Complete it in the
              gateway's checkout to receive the credits.
            </div>
          }
        </section>

        @if (reconciliation(); as recon) {
          <div class="card stack">
            <h2>Reconciliation</h2>
            <p class="muted">
              The stored balances are a cache. These figures are recomputed by replaying every
              ledger entry — they must agree.
            </p>

            @if (recon.matches) {
              <p class="ok">✓ Stored balances match a full replay of the ledger.</p>
            } @else {
              <div class="banner banner--error" role="alert">
                <strong>Mismatch.</strong> Replay gives
                {{ recon.replayedSpendable | currency: 'INR' : 'symbol' : '1.2-2' }} spendable /
                {{ recon.replayedHeld | currency: 'INR' : 'symbol' : '1.2-2' }} held /
                {{ recon.replayedEarning | currency: 'INR' : 'symbol' : '1.2-2' }} earning. Treat
                this as an incident.
              </div>
            }
          </div>
        }

        <section class="card stack">
          <h2>Transaction history</h2>

          @if (entries().length === 0) {
            <p class="muted">No movements yet.</p>
          } @else {
            <div class="table-scroll">
              <table>
                <thead>
                  <tr>
                    <th>When</th>
                    <th>Type</th>
                    <th>Account</th>
                    <th class="numeric">Amount</th>
                    <th>Description</th>
                  </tr>
                </thead>
                <tbody>
                  @for (entry of entries(); track $index) {
                    <tr>
                      <td>{{ entry.createdAt | date: 'short' }}</td>
                      <td>{{ entry.transactionType }}</td>
                      <td>{{ entry.account }}</td>
                      <td class="numeric" [class.debit]="entry.direction === 'Debit'">
                        {{ entry.direction === 'Debit' ? '−' : '+'
                        }}{{ entry.amount | currency: 'INR' : 'symbol' : '1.2-2' }}
                      </td>
                      <td class="muted">{{ entry.description ?? '—' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
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

      .small {
        font-size: 0.8rem;
      }

      .ok {
        color: var(--positive);
        margin: 0;
      }

      .debit {
        color: var(--negative);
      }

      form.row {
        align-items: flex-end;
      }

      .amount {
        max-width: 200px;
      }
    `,
  ],
})
export class WalletComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly wallet = signal<Wallet | null>(null);
  readonly reconciliation = signal<Reconciliation | null>(null);
  readonly entries = signal<LedgerEntrySummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  topUpAmount = 500;
  readonly paying = signal(false);
  readonly settling = signal(false);
  readonly paymentError = signal<string | null>(null);
  readonly pendingOrder = signal<StartPaymentResult | null>(null);
  readonly outcome = signal<PaymentOrderView | null>(null);

  /**
   * How long to keep asking before giving up. About two minutes: a real UPI payment can take a
   * while to confirm, and the server asks the gateway itself while we wait, so a payment whose
   * webhook never arrives still shows up here.
   */
  private static readonly POLL_INTERVAL_MS = 1200;
  private static readonly POLL_ATTEMPTS = 100;

  startPayment(): void {
    this.paying.set(true);
    this.paymentError.set(null);
    this.pendingOrder.set(null);
    this.outcome.set(null);

    this.api.startPayment(this.topUpAmount).subscribe({
      next: (order) => {
        this.paying.set(false);

        const checkoutUrl = this.checkoutUrlFor(order);

        if (checkoutUrl) {
          // Hand the browser to the gateway. We come back to this page with ?orderId=… and find
          // out what happened by asking the server, not by reading the redirect.
          window.location.href = checkoutUrl;
          return;
        }

        // A payload with no checkout URL. Every gateway the API offers sends one; show the order.
        this.pendingOrder.set(order);
      },
      error: (err: Error) => {
        this.paying.set(false);
        // With no gateway configured the API says so plainly; surface that rather than a generic
        // failure, because it is a deployment gap and not the user's mistake.
        this.paymentError.set(err.message);
      },
    });
  }

  /** The gateway's hosted page, or null when this gateway expects its SDK to be used instead. */
  private checkoutUrlFor(order: StartPaymentResult): string | null {
    let payload: CheckoutPayload;

    try {
      payload = JSON.parse(order.checkoutPayload) as CheckoutPayload;
    } catch {
      return null;
    }

    if (!payload.checkout_url) {
      return null;
    }

    // The gateway hands back a path relative to its own host, which for the sandbox is the API.
    return payload.checkout_url.startsWith('http')
      ? payload.checkout_url
      : `${environment.apiBaseUrl}${payload.checkout_url}`;
  }

  /**
   * Asks the server what became of an order until it stops being Created. The redirect back from
   * checkout only proves the user returned - the credits exist because a signed callback was
   * verified, and this is the only thing that knows whether that happened.
   */
  private awaitSettlement(orderId: string): void {
    this.settling.set(true);

    timer(0, WalletComponent.POLL_INTERVAL_MS)
      .pipe(
        // catchError sits inside the switchMap on purpose: out here it would swallow the error by
        // completing the whole stream, so one blocked request would end the wait instead of
        // costing it a single attempt.
        switchMap(() => this.api.paymentOrder(orderId).pipe(catchError(() => of(null)))),
        takeWhile((order) => order === null || order.status === 'Created', true),
        take(WalletComponent.POLL_ATTEMPTS),
      )
      .subscribe({
        next: (order) => {
          if (!order) {
            return;
          }

          this.outcome.set(order);

          if (order.status !== 'Created') {
            this.settling.set(false);
            this.reload();
          }
        },
        complete: () => this.settling.set(false),
        error: (err: Error) => {
          this.settling.set(false);
          this.paymentError.set(err.message);
        },
      });
  }

  private reload(): void {
    const userId = this.auth.userId();

    forkJoin({
      wallet: this.api.myWallet(),
      entries: this.api.myTransactions(100),
      reconciliation: this.api.reconcile(userId!),
    }).subscribe({
      next: (result) => {
        this.wallet.set(result.wallet);
        this.entries.set(result.entries);
        this.reconciliation.set(result.reconciliation);
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }

  ngOnInit(): void {
    this.reload();

    const orderId = this.route.snapshot.queryParamMap.get('orderId');

    if (orderId) {
      // Drop the parameter so a refresh does not replay the wait for an order already resolved.
      this.router.navigate([], { queryParams: {}, replaceUrl: true });
      this.awaitSettlement(orderId);
    }
  }
}
