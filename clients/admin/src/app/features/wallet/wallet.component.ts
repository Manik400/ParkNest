import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
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
  imports: [DatePipe, DecimalPipe, FormsModule, RouterLink],
  template: `
    <div class="page-head">
      <h1>Wallet</h1>
    </div>

    @if (error(); as message) {
      <div class="banner banner--error" role="alert">{{ message }}</div>
    }

    @if (loading()) {
      <p class="muted">Loading…</p>
    } @else {
      <section class="balance card">
        <div class="overline">Your balance</div>
        <div class="big">₹{{ wallet()?.spendable ?? 0 | number: '1.0-0' }}</div>
        <div class="lines">
          @if ((wallet()?.held ?? 0) > 0) {
            <div>₹{{ wallet()?.held | number: '1.0-0' }} reserved for your upcoming bookings</div>
          }
          @if ((wallet()?.earning ?? 0) > 0) {
            <div>₹{{ wallet()?.earning | number: '1.0-0' }} earned from your spaces · <a routerLink="/payouts">withdraw</a></div>
          }
          @if ((wallet()?.held ?? 0) === 0 && (wallet()?.earning ?? 0) === 0) {
            <div class="muted">Nothing reserved right now.</div>
          }
        </div>
      </section>

      <section class="card topup">
        <h2>Add money</h2>
        <p class="muted">Pay by UPI. The money lands in your balance the moment the payment is confirmed.</p>

        @if (settling()) {
          <div class="banner banner--info" role="status">Confirming your payment… this usually takes a few seconds.</div>
        }

        @if (outcome(); as order) {
          @if (order.status === 'Paid') {
            <div class="banner banner--success" role="status">✓ ₹{{ order.amount | number: '1.0-0' }} added to your balance.</div>
          } @else if (order.status === 'Failed') {
            <div class="banner banner--error" role="alert">
              <strong>Payment didn't go through</strong>
              Nothing was charged. {{ order.failureReason }}
            </div>
          } @else {
            <div class="banner banner--info" role="status">
              We haven't heard back about this payment yet. If you completed it, the money will appear shortly.
            </div>
          }
        }

        <form (ngSubmit)="startPayment()">
          <div class="quick">
            @for (q of quickAmounts; track q) {
              <button type="button" class="chip" [class.selected]="topUpAmount === q" (click)="topUpAmount = q">₹{{ q }}</button>
            }
          </div>
          <div class="amount-row">
            <div class="amount">
              <label for="amount">Or another amount</label>
              <input id="amount" name="amount" type="number" min="100" step="100" [(ngModel)]="topUpAmount" />
            </div>
            @if (phoneNeeded()) {
              <!-- Shown only after the gateway asked for it: most accounts have a number on file.
                   Saved on the account once used, and editable on the profile page. -->
              <div class="amount">
                <label for="phone">Mobile number</label>
                <input id="phone" name="phone" type="tel" inputmode="tel" placeholder="10 digits"
                       autocomplete="tel" [(ngModel)]="topUpPhone" required />
              </div>
            }
            <button class="primary" type="submit" [disabled]="paying() || settling()">
              {{ paying() ? 'Opening checkout…' : 'Add ₹' + topUpAmount }}
            </button>
          </div>
        </form>

        @if (paymentError(); as message) {
          <div class="banner banner--error" role="alert">{{ message }}</div>
        }

        @if (pendingOrder(); as order) {
          <!-- Every gateway the API offers returns a checkout URL, so this is a fallback for a
               payload without one: show the order rather than nothing. -->
          <div class="banner banner--info">
            Order <strong>{{ order.providerOrderId }}</strong> created for ₹{{ order.amount | number: '1.0-0' }}.
            Complete it in the gateway's checkout to receive the money.
          </div>
        }
      </section>

      <!-- Operators see the ledger replay; renters never need to know balances are cached. -->
      @if (reconciliation(); as recon) {
        @if (!recon.matches) {
          <div class="banner banner--error" role="alert">
            <strong>Balance check failed</strong>
            Replay gives ₹{{ recon.replayedSpendable | number: '1.0-0' }} spendable /
            ₹{{ recon.replayedHeld | number: '1.0-0' }} reserved / ₹{{ recon.replayedEarning | number: '1.0-0' }} earned.
            Treat this as an incident.
          </div>
        } @else if (isAdmin) {
          <p class="muted small">✓ Stored balances match a full replay of the ledger.</p>
        }
      }

      <section class="history">
        <h2>Activity</h2>
        @if (entries().length === 0) {
          <div class="empty">
            <div class="empty__mark"></div>
            <h3>No activity yet</h3>
            <p>Money you add, reserve and earn will show up here.</p>
          </div>
        } @else {
          <div class="entries">
            @for (entry of entries(); track $index) {
              <div class="entry">
                <div>
                  <div class="what">{{ describe(entry) }}</div>
                  <div class="muted small">{{ entry.createdAt | date: 'd MMM, h:mm a' }}</div>
                </div>
                <div class="amt" [class.debit]="entry.direction === 'Debit'">
                  {{ entry.direction === 'Debit' ? '−' : '+' }}₹{{ entry.amount | number: '1.0-0' }}
                </div>
              </div>
            }
          </div>
        }
      </section>
    }
  `,
  styles: [
    `
      :host {
        display: block;
        max-width: 720px;
      }

      .balance {
        margin-bottom: 16px;
      }

      .big {
        font: 600 44px/1.05 var(--font-display);
        letter-spacing: -0.03em;
        margin: 8px 0 12px;
      }

      .lines {
        display: flex;
        flex-direction: column;
        gap: 4px;
        font-size: 14px;
        color: var(--ink-soft);
      }

      .topup {
        margin-bottom: 24px;
      }

      .topup h2 {
        margin-bottom: 6px;
      }

      .quick {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
        margin: 16px 0;
      }

      .amount-row {
        display: flex;
        gap: 12px;
        align-items: flex-end;
        flex-wrap: wrap;
      }

      .amount {
        flex: 1 1 160px;
        max-width: 220px;
      }

      .history h2 {
        margin-bottom: 14px;
      }

      .entries {
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: 18px;
        overflow: hidden;
      }

      .entry {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 14px;
        padding: 14px 18px;
        border-top: 1px solid var(--hairline);
      }

      .entry:first-child {
        border-top: 0;
      }

      .what {
        font-weight: 600;
      }

      .amt {
        font: 600 17px/1 var(--font-display);
        letter-spacing: -0.01em;
        color: var(--success-ink);
        white-space: nowrap;
      }

      .amt.debit {
        color: var(--ink);
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

  readonly quickAmounts = [200, 500, 1000, 2000];
  readonly isAdmin = this.auth.isAdmin();

  topUpAmount = 500;
  topUpPhone = '';
  /** The gateway refused to open a checkout without a mobile number, and the account has none. */
  readonly phoneNeeded = signal(false);
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

  /** The ledger's vocabulary (LedgerTransactionType), translated once. */
  describe(entry: LedgerEntrySummary): string {
    switch (entry.transactionType) {
      case 'Recharge':
        return 'Money added';
      case 'Hold':
        return 'Reserved for a booking';
      case 'ReleaseHold':
        return 'Unused time returned';
      case 'OverstayDebit':
        return 'Extra time charged';
      case 'Settlement':
        return entry.direction === 'Credit' ? 'Earned from a booking' : 'Parking charged';
      case 'Payout':
        return 'Withdrawn';
      case 'Refund':
        return 'Refunded';
      case 'AdminAdjustment':
        return entry.description ?? 'Adjustment by ParkNest';
      default:
        return entry.description ?? entry.transactionType.replace(/([a-z])([A-Z])/g, '$1 $2');
    }
  }

  startPayment(): void {
    this.paying.set(true);
    this.paymentError.set(null);
    this.pendingOrder.set(null);
    this.outcome.set(null);

    this.api.startPayment(this.topUpAmount, this.phoneNeeded() ? this.topUpPhone.trim() : undefined).subscribe({
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

        // The API's problem type is not carried through the interceptor, only its message, so
        // the phrase the PaymentPhoneRequiredException always uses is what identifies it here.
        if (/mobile number/i.test(err.message) && !this.phoneNeeded()) {
          this.phoneNeeded.set(true);
        }

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
