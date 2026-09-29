import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, Input, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import { ApiService } from '../../core/api.service';
import { BookingDetail } from '../../core/models';
import { describeMoney } from './booking-money';

/**
 * A receipt: the overview, the breakdown, and how it ended, on one page that prints cleanly.
 *
 * Print is the PDF. Every browser saves a printed page as PDF, and a receipt generated in the
 * browser from the same figures the booking page shows cannot disagree with it — where a
 * server-rendered PDF would be a second rendering of the same money to keep in step.
 */
@Component({
  selector: 'app-receipt',
  standalone: true,
  imports: [CurrencyPipe, DatePipe, RouterLink],
  template: `
    <div class="wrap">
      <div class="toolbar no-print">
        <a [routerLink]="['/bookings', bookingId]">← Back to the booking</a>
        <button type="button" class="primary" (click)="print()">Print / save as PDF</button>
      </div>

      @if (error(); as message) {
        <div class="banner banner--error" role="alert">{{ message }}</div>
      }

      @if (detail(); as booking) {
        @if (money(); as m) {
          <article class="receipt">
            <header>
              <div class="brand">
                <span class="mark"><span></span></span>
                <span class="wordmark">ParkNest</span>
              </div>
              <div class="meta">
                <div class="overline">Receipt</div>
                <div class="muted small">Booking {{ booking.summary.id }}</div>
                <div class="muted small">Issued {{ now | date: 'd MMM y, h:mm a' }}</div>
              </div>
            </header>

            <section class="overview">
              <div>
                <div class="overline">Space</div>
                <div class="strong">{{ booking.summary.spaceTitle }}</div>
                <div class="muted">{{ booking.summary.spaceAddress }}</div>
              </div>
              <div>
                <div class="overline">When</div>
                <div class="strong">{{ booking.summary.startTime | date: 'EEE d MMM y' }}</div>
                <div class="muted">
                  {{ booking.summary.startTime | date: 'h:mm a' }} → {{ booking.summary.expectedEndTime | date: 'h:mm a' }}
                  · {{ booking.bookedMinutes }} min booked
                  @if (booking.billedMinutes != null) {
                    · {{ booking.billedMinutes }} min billed
                  }
                </div>
              </div>
              <div>
                <div class="overline">Vehicle</div>
                <div class="strong">{{ booking.vehiclePlate }}</div>
                <div class="muted">Rate {{ booking.summary.ratePerHour | currency: 'INR' : 'symbol' : '1.0-0' }}/hr</div>
              </div>
            </section>

            <section class="total" [class]="'total tone-' + m.tone">
              <div class="overline">{{ m.headlineLabel }}</div>
              <div class="big">{{ m.headline | currency: 'INR' : 'symbol' : '1.2-2' }}</div>
              <div class="status">{{ m.statusLabel }}</div>
            </section>

            <section>
              <h2>Breakdown</h2>
              <table class="lines">
                <tbody>
                  @for (line of m.lines; track line.label) {
                    <tr [class.emphasis]="line.emphasis">
                      <td>
                        {{ line.label }}
                        @if (line.note) {
                          <span class="muted small">· {{ line.note }}</span>
                        }
                      </td>
                      <td class="numeric">{{ line.amount | currency: 'INR' : 'symbol' : '1.2-2' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </section>

            <section>
              <h2>Transactions</h2>
              @if (transactions().length === 0) {
                <p class="muted">None yet — nothing has moved.</p>
              } @else {
                <table class="lines">
                  <tbody>
                    @for (t of transactions(); track t.reference) {
                      <tr>
                        <td>
                          <code>{{ t.reference }}</code> · {{ t.type }}
                          @if (t.revertsReference) {
                            <span class="muted small">· reverses <code>{{ t.revertsReference }}</code></span>
                          }
                          <div class="muted small">{{ t.createdAt | date: 'd MMM y, h:mm:ss a' }}</div>
                        </td>
                        <td class="numeric">{{ t.amount | currency: 'INR' : 'symbol' : '1.2-2' }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            </section>

            <section class="conclusion" [class]="'conclusion tone-' + m.tone">
              <div class="overline">Outcome</div>
              <p><strong>{{ m.statusLabel }}.</strong> {{ m.conclusion }}</p>
            </section>

            <footer class="muted small">
              Amounts are in ParkNest credits (₹1 = 1 credit). Quote a transaction reference when
              asking about a line. Nothing on this receipt is edited after the fact: a correction is
              always a new transaction that points at the one it corrects.
            </footer>
          </article>
        }
      } @else if (!error()) {
        <p class="muted">Loading…</p>
      }
    </div>
  `,
  styles: [
    `
      .wrap {
        max-width: 760px;
        margin: 0 auto;
      }

      .toolbar {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 12px;
        margin-bottom: 20px;
      }

      .receipt {
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: var(--r-panel);
        padding: clamp(20px, 4vw, 40px);
        display: flex;
        flex-direction: column;
        gap: 28px;
      }

      header {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
        gap: 16px;
        flex-wrap: wrap;
      }

      .brand {
        display: flex;
        align-items: center;
        gap: 9px;
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
        background: #fff;
      }

      .wordmark {
        font: 700 20px/1 var(--font-display);
        letter-spacing: -0.02em;
      }

      .meta {
        text-align: right;
      }

      .overview {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
        gap: 16px;
      }

      .strong {
        font-weight: 700;
        margin-top: 4px;
      }

      .total {
        border-radius: var(--r-card);
        padding: 18px 22px;
        background: var(--sunken);
        border-left: 6px solid var(--border-strong);
      }

      .tone-good {
        border-left-color: var(--success-ink);
      }

      .tone-warn {
        border-left-color: var(--warn-ink);
      }

      .tone-bad {
        border-left-color: var(--danger-ink);
      }

      .big {
        font: 600 clamp(34px, 5vw, 48px) / 1.05 var(--font-display);
        letter-spacing: -0.03em;
        margin: 4px 0;
      }

      .status {
        font-weight: 700;
      }

      h2 {
        font-size: 17px;
        margin-bottom: 8px;
      }

      .lines {
        width: 100%;
        border-collapse: collapse;
      }

      .lines td {
        padding: 9px 0;
        border-top: 1px solid var(--hairline);
        vertical-align: top;
      }

      .lines tr:first-child td {
        border-top: 0;
      }

      .lines .emphasis td {
        font-weight: 700;
      }

      .numeric {
        text-align: right;
        font-variant-numeric: tabular-nums;
        white-space: nowrap;
      }

      .conclusion {
        padding: 14px 18px;
        border-radius: var(--r-card);
        background: var(--sunken);
        border-left: 6px solid var(--border-strong);
      }

      .conclusion p {
        margin: 4px 0 0;
      }

      code {
        font-family: ui-monospace, 'SFMono-Regular', Menlo, monospace;
        font-size: 12.5px;
      }

      @media print {
        .wrap {
          max-width: none;
        }

        .receipt {
          border: 0;
          padding: 0;
          gap: 20px;
        }

        .total,
        .conclusion {
          border: 1px solid #ccc;
          border-left-width: 6px;
          background: #fff;
        }
      }
    `,
  ],
})
export class ReceiptComponent implements OnInit {
  private readonly api = inject(ApiService);

  @Input() bookingId = '';

  readonly detail = signal<BookingDetail | null>(null);
  readonly error = signal<string | null>(null);
  readonly now = new Date();

  readonly money = computed(() => {
    const d = this.detail();
    return d ? describeMoney(d) : null;
  });

  /** One row per transaction, sized by its debits. */
  readonly transactions = computed(() => {
    const entries = this.detail()?.ledgerEntries ?? [];
    const seen = new Map<string, { reference: string; revertsReference: string | null; type: string; createdAt: string; amount: number }>();

    for (const e of entries) {
      const t = seen.get(e.transactionId) ?? {
        reference: e.reference,
        revertsReference: e.revertsReference,
        type: e.transactionType.replace(/([a-z])([A-Z])/g, '$1 $2'),
        createdAt: e.createdAt,
        amount: 0,
      };
      if (e.direction === 'Debit') {
        t.amount += e.amount;
      }
      seen.set(e.transactionId, t);
    }

    return [...seen.values()];
  });

  ngOnInit(): void {
    this.api.booking(this.bookingId).subscribe({
      next: (detail) => this.detail.set(detail),
      error: (err: Error) => this.error.set(err.message),
    });
  }

  print(): void {
    window.print();
  }
}
