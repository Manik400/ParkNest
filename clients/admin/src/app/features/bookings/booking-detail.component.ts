import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, Input, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { ApiService } from '../../core/api.service';
import { BookingDetail, CancellationTerms, LedgerEntrySummary } from '../../core/models';
import { StatusPillComponent } from '../../shared/status-pill.component';
import { describeMoney } from './booking-money';

/** The ledger trail grouped by transaction: one row per movement, its legs beneath it. */
interface TrailGroup {
  reference: string;
  revertsReference: string | null;
  type: string;
  createdAt: string;
  entries: LedgerEntrySummary[];
}

/**
 * One booking. The strip at the top answers the three questions anyone opens this page with —
 * what state is it in, when is it, what did it cost — before the detail below explains how.
 */
@Component({
  selector: 'app-booking-detail',
  standalone: true,
  imports: [CurrencyPipe, DatePipe, RouterLink, StatusPillComponent, FormsModule],
  template: `
    <div class="stack">
      <a routerLink="/bookings">← Back to bookings</a>

      @if (error(); as message) {
        <div class="banner banner--error" role="alert">{{ message }}</div>
      }

      @if (loading()) {
        <p class="muted">Loading…</p>
      }

      @if (detail(); as booking) {
        @if (money(); as m) {
          <!-- The answer first. Status, when, and the one number — then the sentence that says
               how it ended. Everything below is the working. -->
          <section class="summary" [class]="'summary tone-' + m.tone">
            <div class="summary-main">
              <div>
                <div class="overline">{{ m.statusLabel }}</div>
                <h1>{{ booking.summary.spaceTitle }}</h1>
                <div class="muted">{{ booking.summary.spaceAddress }}</div>
              </div>
              <div class="figure">
                <div class="overline">{{ m.headlineLabel }}</div>
                <div class="big">{{ m.headline | currency: 'INR' : 'symbol' : '1.0-0' }}</div>
              </div>
            </div>
            <div class="summary-foot">
              <div class="when">
                <strong>{{ booking.summary.startTime | date: 'EEE d MMM, h:mm a' }}</strong>
                → {{ booking.summary.expectedEndTime | date: 'h:mm a' }}
                <span class="muted">· {{ booking.bookedMinutes }} min booked</span>
              </div>
              <p class="conclusion">{{ m.conclusion }}</p>
              <a class="btn sm" [routerLink]="['/bookings', bookingId, 'receipt']">Receipt</a>
            </div>
          </section>
        }

        <!-- The session controls. Tier 1 detection is the renter tapping these; Tier 2/3 will
             replace them with a QR scan or ANPR without changing what happens to the money. -->
        @if (booking.summary.status === 'Held' || booking.summary.status === 'Active') {
          <div class="card row actions">
            @if (booking.summary.status === 'Held') {
              <button class="primary" type="button" [disabled]="busy()" (click)="checkIn()">
                I have parked
              </button>
              <button type="button" [disabled]="busy()" (click)="cancel()">Cancel booking</button>
              @if (cancellation(); as terms) {
                <span class="muted small">
                  @if (terms.slotBlocked) {
                    <!-- Free for a different reason, and the reason is the message. A renter told
                         only "this is free" will assume good timing rather than that the space
                         they are driving to has somebody else's car in it. -->
                    The space was still occupied when this slot came due. Cancelling returns the
                    whole hold, however late it is.
                  } @else if (terms.isFree) {
                    Cancelling now returns the whole hold.
                  } @else {
                    <!-- The fee is a server-side policy; showing the figure it will actually
                         charge beats restating a rule that can change without a deploy. -->
                    Cancelling now costs
                    {{ terms.fee | currency: 'INR' : 'symbol' : '1.2-2' }} — the host cannot re-let
                    the slot this close to the start.
                  }
                </span>
              }
            } @else {
              <button class="primary" type="button" [disabled]="busy()" (click)="checkOut()">
                I am leaving
              </button>
              <span class="muted small">
                Checking out meters the real duration and settles it. Staying past
                {{ booking.summary.expectedEndTime | date: 'shortTime' }} bills the overstay
                automatically.
              </span>
            }
          </div>
        }

        @if (outcome(); as result) {
          <div class="banner banner--info" role="status">
            Settled {{ result.billedMinutes }} minutes for
            {{ result.totalCharged | currency: 'INR' : 'symbol' : '1.2-2' }}.
            Released {{ result.releasedToRenter | currency: 'INR' : 'symbol' : '1.2-2' }} back to you.
          </div>
        }

        <!-- The slot could not be delivered: the previous session had not ended when this one was
             due to start. Called out above the figures because it changes what the renter may do
             — cancelling is free from here on — and because the money below looks unremarkable. -->
        @if (booking.blockedByBookingId) {
          <div class="banner banner--error" role="alert">
            <strong>The space was still occupied when this slot came due.</strong>
            Noticed {{ booking.blockedAt | date: 'medium' }}. The previous session over-ran into
            this booking, so cancelling it costs nothing whatever the notice.
            <a [routerLink]="['/bookings', booking.blockedByBookingId]">
              See the session that over-ran
            </a>
          </div>
        }

        <!-- A shortfall means the renter left owing credits. It is the single most important
             thing on this screen, so it is called out rather than buried in the figures. -->
        @if (booking.shortfallAmount > 0) {
          <div class="banner banner--error" role="alert">
            <strong>
              {{ booking.shortfallAmount | currency: 'INR' : 'symbol' : '1.2-2' }} uncovered.
            </strong>
            The renter could not cover the overstay. Their access is restricted until it is
            resolved; the host received everything that could be collected.
          </div>
        }

        @if (money(); as m) {
          <section class="card">
            <h2>The money</h2>
            <p class="muted small">
              Rate {{ booking.summary.ratePerHour | currency: 'INR' : 'symbol' : '1.0-0' }}/hr.
              Every line below is a movement the ledger actually made; the raw trail is further down.
            </p>
            <ul class="lines">
              @for (line of m.lines; track line.label) {
                <li [class.emphasis]="line.emphasis">
                  <span class="label">
                    {{ line.label }}
                    @if (line.note) {
                      <span class="muted small">· {{ line.note }}</span>
                    }
                  </span>
                  <span class="amount">{{ line.amount | currency: 'INR' : 'symbol' : '1.2-2' }}</span>
                </li>
              }
            </ul>
          </section>
        }

        <section class="card">
          <h2>Session</h2>
          <dl>
            <div><dt>Vehicle</dt><dd>{{ booking.vehiclePlate }}</dd></div>
            <div><dt>Booked start</dt><dd>{{ booking.summary.startTime | date: 'medium' }}</dd></div>
            <div>
              <dt>Expected end</dt>
              <dd>{{ booking.summary.expectedEndTime | date: 'medium' }}</dd>
            </div>
            <div>
              <dt>Checked in</dt>
              <dd>{{ booking.actualStartTime ? (booking.actualStartTime | date: 'medium') : '—' }}</dd>
            </div>
            <div>
              <dt>Checked out</dt>
              <dd>
                {{ booking.summary.actualEndTime ? (booking.summary.actualEndTime | date: 'medium') : '—' }}
              </dd>
            </div>
            <div><dt>Booked minutes</dt><dd>{{ booking.bookedMinutes }}</dd></div>
            <div><dt>Billed minutes</dt><dd>{{ booking.billedMinutes ?? '—' }}</dd></div>
            <div>
              <dt>Detection</dt>
              <dd>{{ booking.startDetectionMethod ?? '—' }} → {{ booking.endDetectionMethod ?? '—' }}</dd>
            </div>
          </dl>
        </section>

        <!-- Only once something has actually happened: a booking still in Held has nothing to
             dispute and should be cancelled instead, which is what the API says too. -->
        @if (booking.summary.status !== 'Held' && booking.summary.status !== 'Cancelled') {
          <section class="card stack">
            <h2>Something wrong?</h2>

            @if (disputeRaised()) {
              <div class="banner banner--info" role="status">
                Dispute raised. You can follow it under
                <a routerLink="/disputes">My disputes</a>.
              </div>
            } @else {
              <p class="muted">
                Raise a dispute and an operator will review it. Any refund is posted as a new
                transaction — nothing on this page is rewritten.
              </p>

              <div>
                <label for="disputeReason">What went wrong</label>
                <textarea
                  id="disputeReason"
                  name="disputeReason"
                  rows="3"
                  [(ngModel)]="disputeReason"
                ></textarea>
              </div>

              <div class="row actions">
                <button
                  type="button"
                  [disabled]="busy() || !disputeReason.trim()"
                  (click)="raiseDispute()"
                >
                  Raise a dispute
                </button>
              </div>
            }
          </section>
        }

        <section class="card stack">
          <div class="trail-head">
            <div>
              <h2>Ledger trail</h2>
              <p class="muted">
                Every credit movement for this booking, with the reference to quote if you need
                to ask about one.
              </p>
            </div>
            @if (booking.ledgerEntries.length > 0) {
              <button type="button" class="ghost sm" (click)="showLegs.set(!showLegs())">
                {{ showLegs() ? 'Hide the legs' : 'Show every leg' }}
              </button>
            }
          </div>

          @if (booking.ledgerEntries.length === 0) {
            <p class="muted">No movements yet — credits are reserved but nothing has settled.</p>
          } @else {
            <div class="trail">
              @for (group of trail(); track group.reference) {
                <div class="txn">
                  <div class="txn-head">
                    <div>
                      <div class="txn-type">{{ describeType(group.type) }}</div>
                      <div class="muted small">
                        {{ group.createdAt | date: 'd MMM, h:mm:ss a' }} · <code>{{ group.reference }}</code>
                        @if (group.revertsReference) {
                          · reverses <code>{{ group.revertsReference }}</code>
                        }
                      </div>
                    </div>
                    <div class="txn-amount">
                      {{ groupAmount(group) | currency: 'INR' : 'symbol' : '1.2-2' }}
                    </div>
                  </div>
                  @if (showLegs()) {
                    <table class="legs">
                      <tbody>
                        @for (entry of group.entries; track entry.account + entry.direction) {
                          <tr>
                            <td>{{ entry.account }}</td>
                            <td [class.debit]="entry.direction === 'Debit'">{{ entry.direction }}</td>
                            <td class="numeric">{{ entry.amount | currency: 'INR' : 'symbol' : '1.2-2' }}</td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  }
                </div>
              }
            </div>
          }
        </section>
      }
    </div>
  `,
  styles: [
    `
      .summary {
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: var(--r-panel);
        padding: 22px clamp(18px, 3vw, 28px);
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

      .summary-main {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
        gap: 24px;
        flex-wrap: wrap;
      }

      .summary h1 {
        margin: 4px 0 4px;
      }

      .figure {
        text-align: right;
      }

      .big {
        font: 600 clamp(30px, 4vw, 40px) / 1.05 var(--font-display);
        letter-spacing: -0.03em;
        margin-top: 4px;
      }

      .summary-foot {
        margin-top: 18px;
        padding-top: 16px;
        border-top: 1px solid var(--hairline);
        display: grid;
        grid-template-columns: 1fr auto;
        gap: 6px 16px;
        align-items: center;
      }

      .when {
        grid-column: 1;
      }

      .conclusion {
        grid-column: 1;
        margin: 0;
        color: var(--ink-soft);
        max-width: 64ch;
      }

      .summary-foot .btn {
        grid-column: 2;
        grid-row: 1 / span 2;
      }

      .lines {
        list-style: none;
        margin: 12px 0 0;
        padding: 0;
        max-width: 620px;
      }

      .lines li {
        display: flex;
        justify-content: space-between;
        gap: 16px;
        padding: 10px 0;
        border-top: 1px solid var(--hairline);
      }

      .lines li:first-child {
        border-top: 0;
      }

      .lines .amount {
        font-variant-numeric: tabular-nums;
        white-space: nowrap;
      }

      .lines .emphasis {
        font-weight: 700;
      }

      dl {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
        gap: var(--space-3);
        margin: 0;
      }

      dt {
        font-size: 0.78rem;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: var(--text-muted);
      }

      dd {
        margin: 0;
        font-variant-numeric: tabular-nums;
      }

      .debit {
        color: var(--negative);
      }

      .actions {
        align-items: center;
      }

      .small {
        font-size: 0.8rem;
      }

      .trail-head {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
        gap: 12px;
        flex-wrap: wrap;
      }

      .trail {
        display: flex;
        flex-direction: column;
        gap: 10px;
      }

      .txn {
        border: 1px solid var(--border);
        border-radius: var(--r-card);
        padding: 12px 14px;
      }

      .txn-head {
        display: flex;
        justify-content: space-between;
        gap: 12px;
        align-items: flex-start;
      }

      .txn-type {
        font-weight: 700;
      }

      .txn-amount {
        font-variant-numeric: tabular-nums;
        font-weight: 700;
        white-space: nowrap;
      }

      .legs {
        margin-top: 10px;
        width: 100%;
      }

      .legs td {
        padding: 6px 8px;
        font-size: 13px;
      }

      code {
        font-family: ui-monospace, 'SFMono-Regular', Menlo, monospace;
        font-size: 12px;
        letter-spacing: 0.02em;
      }

      @media (max-width: 620px) {
        .figure {
          text-align: left;
        }

        .summary-foot {
          grid-template-columns: 1fr;
        }

        .summary-foot .btn {
          grid-column: 1;
          grid-row: auto;
          justify-self: start;
        }
      }
    `,
  ],
})
export class BookingDetailComponent implements OnInit {
  private readonly api = inject(ApiService);

  /** Bound from the route by withComponentInputBinding. */
  @Input() bookingId = '';

  readonly detail = signal<BookingDetail | null>(null);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly showLegs = signal(false);
  readonly outcome = signal<{
    billedMinutes: number;
    totalCharged: number;
    releasedToRenter: number;
  } | null>(null);

  readonly disputeRaised = signal(false);

  disputeReason = '';

  readonly cancellation = signal<CancellationTerms | null>(null);

  readonly money = computed(() => {
    const d = this.detail();
    return d ? describeMoney(d) : null;
  });

  /** The trail as transactions rather than legs: one row per thing that happened. */
  readonly trail = computed<TrailGroup[]>(() => {
    const entries = this.detail()?.ledgerEntries ?? [];
    const groups = new Map<string, TrailGroup>();

    for (const entry of entries) {
      const group = groups.get(entry.transactionId) ?? {
        reference: entry.reference,
        revertsReference: entry.revertsReference,
        type: entry.transactionType,
        createdAt: entry.createdAt,
        entries: [],
      };
      group.entries.push(entry);
      groups.set(entry.transactionId, group);
    }

    return [...groups.values()];
  });

  ngOnInit(): void {
    this.load();
  }

  /** A transaction's size: the debits, which equal the credits. */
  groupAmount(group: TrailGroup): number {
    return group.entries.filter((e) => e.direction === 'Debit').reduce((sum, e) => sum + e.amount, 0);
  }

  describeType(type: string): string {
    switch (type) {
      case 'Hold':
        return 'Reserved from balance';
      case 'ReleaseHold':
        return 'Returned to balance';
      case 'OverstayDebit':
        return 'Extra time charged';
      case 'Settlement':
        return 'Settled: host paid, fee taken';
      case 'Refund':
        return 'Refunded';
      case 'AdminAdjustment':
        return 'Adjusted after a dispute';
      default:
        return type.replace(/([a-z])([A-Z])/g, '$1 $2');
    }
  }

  raiseDispute(): void {
    this.busy.set(true);
    this.error.set(null);

    this.api.raiseDispute(this.bookingId, this.disputeReason.trim()).subscribe({
      next: () => {
        this.busy.set(false);
        this.disputeRaised.set(true);
        this.disputeReason = '';
      },
      error: (err: Error) => {
        this.busy.set(false);
        this.error.set(err.message);
      },
    });
  }

  checkIn(): void {
    this.act(() => this.api.startSession(this.bookingId));
  }

  checkOut(): void {
    this.busy.set(true);
    this.error.set(null);

    this.api.endSession(this.bookingId).subscribe({
      next: (result) => {
        this.busy.set(false);
        this.outcome.set(result);
        this.load();
      },
      error: (err: Error) => {
        this.busy.set(false);
        this.error.set(err.message);
      },
    });
  }

  cancel(): void {
    this.act(() => this.api.cancelBooking(this.bookingId));
  }

  private act(action: () => { subscribe: (o: object) => unknown }): void {
    this.busy.set(true);
    this.error.set(null);

    action().subscribe({
      next: () => {
        this.busy.set(false);
        this.load();
      },
      error: (err: Error) => {
        this.busy.set(false);
        this.error.set(err.message);
      },
    });
  }

  private load(): void {
    this.api.booking(this.bookingId).subscribe({
      next: (detail) => {
        this.detail.set(detail);
        this.loading.set(false);

        // Only a booking that has not started can be cancelled, so the terms are only worth
        // fetching for one — and a 400 from asking about any other would surface as an error
        // banner on a page where nothing is wrong.
        if (detail.summary.status === 'Held') {
          this.api.cancellationTerms(this.bookingId).subscribe({
            next: (terms) => this.cancellation.set(terms),
            // Silent: the page is fine without it, and the confirmation is advisory. The charge
            // itself is decided server-side when Cancel is actually pressed.
            error: () => this.cancellation.set(null),
          });
        }
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }
}
