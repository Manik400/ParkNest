import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, Input, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { ApiService } from '../../core/api.service';
import { BookingDetail, CancellationTerms } from '../../core/models';
import { StatusPillComponent } from '../../shared/status-pill.component';

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
        <div class="row space-between">
          <h1>{{ booking.summary.spaceTitle }}</h1>
          <app-status-pill [status]="booking.summary.status" />
        </div>

        <p class="muted">{{ booking.summary.spaceAddress }}</p>

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
                  @if (terms.isFree) {
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

        <section class="card">
          <h2>Money</h2>
          <dl>
            <div>
              <dt>Rate</dt>
              <dd>{{ booking.summary.ratePerHour | currency: 'INR' : 'symbol' : '1.2-2' }} / hr</dd>
            </div>
            <div>
              <dt>Held up front</dt>
              <dd>{{ booking.summary.holdAmount | currency: 'INR' : 'symbol' : '1.2-2' }}</dd>
            </div>
            <div>
              <dt>Overstay</dt>
              <dd>{{ booking.overstayAmount | currency: 'INR' : 'symbol' : '1.2-2' }}</dd>
            </div>
            <div>
              <dt>Settled</dt>
              <dd>{{ booking.summary.settledAmount | currency: 'INR' : 'symbol' : '1.2-2' }}</dd>
            </div>
            <div>
              <dt>Platform fee</dt>
              <dd>{{ booking.platformFee | currency: 'INR' : 'symbol' : '1.2-2' }}</dd>
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
          <div>
            <h2>Ledger trail</h2>
            <p class="muted">
              Every credit movement for this booking, in order. Debits and credits balance within
              each transaction.
            </p>
          </div>

          @if (booking.ledgerEntries.length === 0) {
            <p class="muted">No movements yet — credits are reserved but nothing has settled.</p>
          } @else {
            <div class="table-scroll">
              <table>
                <thead>
                  <tr>
                    <th>When</th>
                    <th>Transaction</th>
                    <th>Account</th>
                    <th>Direction</th>
                    <th class="numeric">Amount</th>
                  </tr>
                </thead>
                <tbody>
                  @for (entry of booking.ledgerEntries; track entry.transactionId + entry.account + entry.direction) {
                    <tr>
                      <td>{{ entry.createdAt | date: 'short' }}</td>
                      <td>{{ entry.transactionType }}</td>
                      <td>{{ entry.account }}</td>
                      <td [class.debit]="entry.direction === 'Debit'">{{ entry.direction }}</td>
                      <td class="numeric">{{ entry.amount | currency: 'INR' : 'symbol' : '1.2-2' }}</td>
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
      .space-between {
        justify-content: space-between;
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
  readonly outcome = signal<{
    billedMinutes: number;
    totalCharged: number;
    releasedToRenter: number;
  } | null>(null);

  readonly disputeRaised = signal(false);

  disputeReason = '';

  readonly cancellation = signal<CancellationTerms | null>(null);

  ngOnInit(): void {
    this.load();
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
