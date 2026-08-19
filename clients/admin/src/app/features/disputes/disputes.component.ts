import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { Dispute } from '../../core/models';
import { StatusPillComponent } from '../../shared/status-pill.component';

/**
 * The dispute queue for an admin, and your own disputes for everyone else. One screen, because the
 * list is the same list — what differs is whose disputes are in it and whether the decision
 * controls appear.
 */
@Component({
  selector: 'app-disputes',
  standalone: true,
  imports: [CurrencyPipe, DatePipe, FormsModule, RouterLink, StatusPillComponent],
  template: `
    <div class="stack">
      <div>
        <h1>{{ auth.isAdmin() ? 'Disputes' : 'My disputes' }}</h1>
        <p class="muted">
          @if (auth.isAdmin()) {
            Upholding a dispute posts a new compensating transaction. The booking's original
            settlement is never edited, so both parties keep seeing what they were charged.
          } @else {
            Complaints you raised, or that were raised against a booking of yours.
          }
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
          <label class="inline">
            <input type="checkbox" [(ngModel)]="onlyOpen" (ngModelChange)="load()" />
            Only those awaiting a decision
          </label>
          <button type="button" (click)="load()" [disabled]="loading()">Refresh</button>
        </div>

        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (disputes().length === 0) {
          <p class="muted">Nothing here. A quiet queue is a good queue.</p>
        } @else {
          <div class="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>Raised</th>
                  <th>Booking</th>
                  <th>Reason</th>
                  <th>Status</th>
                  <th class="numeric">Adjustment</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (dispute of disputes(); track dispute.disputeId) {
                  <tr>
                    <td>{{ dispute.createdAt | date: 'd MMM, HH:mm' }}</td>
                    <td>
                      <a [routerLink]="['/bookings', dispute.bookingId]">
                        {{ dispute.bookingId.slice(0, 8) }}
                      </a>
                    </td>
                    <td class="reason">{{ dispute.reason }}</td>
                    <td><app-status-pill [status]="dispute.status" /></td>
                    <td class="numeric">
                      @if (dispute.adjustmentAmount != null) {
                        {{ dispute.adjustmentAmount | currency: 'INR' : 'symbol' : '1.2-2' }}
                      } @else {
                        <span class="muted">—</span>
                      }
                    </td>
                    <td>
                      @if (auth.isAdmin() && isUndecided(dispute)) {
                        <button type="button" (click)="select(dispute)" [disabled]="busy()">
                          Decide
                        </button>
                      } @else if (dispute.resolution) {
                        <span class="muted">{{ dispute.resolution }}</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>

      @if (selected(); as dispute) {
        <section class="card stack">
          <h2>Decide dispute</h2>

          <dl class="facts">
            <div>
              <dt>Reason given</dt>
              <dd>{{ dispute.reason }}</dd>
            </div>
            <div>
              <dt>Raised by</dt>
              <dd>{{ dispute.raisedByUserId === dispute.renterId ? 'The renter' : 'The host' }}</dd>
            </div>
            @if (dispute.evidence.length > 0) {
              <div>
                <dt>Evidence</dt>
                <dd>
                  @for (item of dispute.evidence; track item.id) {
                    <a [href]="item.url" target="_blank" rel="noopener noreferrer">
                      {{ item.note || item.url }}
                    </a>
                  }
                </dd>
              </div>
            }
          </dl>

          <div>
            <label for="resolution">What was decided</label>
            <textarea
              id="resolution"
              name="resolution"
              rows="3"
              [(ngModel)]="resolution"
              placeholder="Both parties read this."
            ></textarea>
          </div>

          <div class="grid">
            <div>
              <label for="refund">Refund to renter</label>
              <input
                id="refund"
                name="refund"
                type="number"
                min="0"
                step="1"
                [(ngModel)]="refund"
              />
              <small class="muted">Zero upholds the complaint without moving any credits.</small>
            </div>

            <div>
              <label for="payer">Charged to</label>
              <select id="payer" name="payer" [(ngModel)]="chargedToPlatform">
                <option [ngValue]="true">The platform (out of commission)</option>
                <option [ngValue]="false">The host (out of earnings)</option>
              </select>
              <!-- Charging the host fails outright once they have cashed out: an earning balance
                   cannot go negative, and the alternative would be inventing credits. -->
              <small class="muted">
                Charging the host is refused if they have already cashed the credits out.
              </small>
            </div>
          </div>

          <div class="actions">
            <button class="primary" type="button" (click)="uphold()" [disabled]="busy()">
              Uphold
            </button>
            <button type="button" (click)="reject()" [disabled]="busy()">Reject</button>
            @if (dispute.status === 'Open') {
              <button type="button" (click)="review()" [disabled]="busy()">
                Mark under review
              </button>
            }
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
        align-items: center;
        justify-content: space-between;
        gap: var(--space-3);
        flex-wrap: wrap;
      }

      .inline {
        display: flex;
        align-items: center;
        gap: var(--space-2);
        font-size: 0.92rem;
      }

      .reason {
        max-width: 26rem;
      }

      .facts {
        display: grid;
        gap: var(--space-3);
      }

      .facts dt {
        font-size: 0.8rem;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: var(--muted);
      }

      .facts dd {
        margin: 0;
        display: flex;
        flex-direction: column;
        gap: var(--space-1);
      }

      .actions {
        display: flex;
        gap: var(--space-2);
        flex-wrap: wrap;
      }
    `,
  ],
})
export class DisputesComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly auth = inject(AuthService);

  readonly disputes = signal<Dispute[]>([]);
  readonly selected = signal<Dispute | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);

  onlyOpen = false;
  resolution = '';
  refund = 0;
  chargedToPlatform = true;

  ngOnInit(): void {
    this.load();
  }

  isUndecided(dispute: Dispute): boolean {
    return dispute.status === 'Open' || dispute.status === 'UnderReview';
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    const source = this.auth.isAdmin()
      ? this.api.adminDisputes(this.onlyOpen)
      : this.api.myDisputes(this.onlyOpen);

    source.subscribe({
      next: (disputes) => {
        this.disputes.set(disputes);
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }

  select(dispute: Dispute): void {
    this.selected.set(dispute);
    this.resolution = '';
    this.refund = 0;
    this.chargedToPlatform = true;
    this.notice.set(null);
  }

  review(): void {
    const dispute = this.selected();
    if (!dispute) {
      return;
    }

    this.run(this.api.reviewDispute(dispute.disputeId), 'Marked under review.');
  }

  uphold(): void {
    const dispute = this.selected();
    if (!dispute) {
      return;
    }

    this.run(
      this.api.resolveDispute(
        dispute.disputeId,
        this.resolution,
        this.refund,
        this.chargedToPlatform,
      ),
      'Dispute upheld.',
    );
  }

  reject(): void {
    const dispute = this.selected();
    if (!dispute) {
      return;
    }

    this.run(this.api.rejectDispute(dispute.disputeId, this.resolution), 'Dispute rejected.');
  }

  private run(source: ReturnType<ApiService['reviewDispute']>, message: string): void {
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);

    source.subscribe({
      next: (updated) => {
        this.busy.set(false);
        this.notice.set(message);
        // Undecided disputes stay open in the panel so a reviewer can keep working; a decided one
        // has nothing left to do.
        this.selected.set(this.isUndecided(updated) ? updated : null);
        this.load();
      },
      error: (err: Error) => {
        this.busy.set(false);
        this.error.set(err.message);
      },
    });
  }
}
