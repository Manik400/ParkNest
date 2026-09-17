import { CurrencyPipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import { ApiService } from '../../core/api.service';
import { DAY_NAMES, ListingDetail, ListingSummary } from '../../core/models';
import { StatusPillComponent } from '../../shared/status-pill.component';

@Component({
  selector: 'app-listings',
  standalone: true,
  imports: [CurrencyPipe, RouterLink, StatusPillComponent],
  template: `
    <div class="stack">
      <div class="row space-between">
        <h1>My listings</h1>
        <a routerLink="/listings/new"><button type="button" class="primary">List a space</button></a>
      </div>

      @if (error(); as message) {
        <div class="banner banner--error" role="alert">{{ message }}</div>
      }

      @if (loading()) {
        <p class="muted">Loading…</p>
      } @else if (listings().length === 0) {
        <p class="muted">
          You are not hosting any spaces yet. <a routerLink="/listings/new">List one</a> and it
          becomes bookable as soon as you publish it.
        </p>
      } @else {
        <div class="card table-scroll">
          <table>
            <thead>
              <tr>
                <th>Title</th>
                <th>City</th>
                <th class="numeric">Rate</th>
                <th class="numeric">Active</th>
                <th>Status</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (listing of listings(); track listing.id) {
                <tr>
                  <td>
                    {{ listing.title }}
                    <div class="muted small">{{ listing.addressLine }}</div>
                  </td>
                  <td>{{ listing.city }}</td>
                  <td class="numeric">{{ listing.pricePerHour | currency: 'INR' : 'symbol' : '1.2-2' }}</td>
                  <td class="numeric">{{ listing.activeBookings }}</td>
                  <td><app-status-pill [status]="listing.status" /></td>
                  <td>
                    <div class="row">
                      <button type="button" (click)="select(listing.id)">Details</button>

                      @if (listing.status === 'Draft' || listing.status === 'Paused') {
                        <button type="button" (click)="publish(listing.id)" [disabled]="busy()">
                          Publish
                        </button>
                      } @else if (listing.status === 'Published') {
                        <button type="button" (click)="setStatus(listing.id, 'Paused')" [disabled]="busy()">
                          Pause
                        </button>
                      }
                    </div>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }

      @if (selected(); as detail) {
        <section class="card stack">
          <div class="row space-between">
            <h2>{{ detail.summary.title }}</h2>
            <button type="button" (click)="selected.set(null)">Close</button>
          </div>

          <dl>
            <div><dt>Time zone</dt><dd>{{ detail.timeZoneId }}</dd></div>
            <div><dt>Zone tier</dt><dd>{{ detail.zone ?? '—' }}</dd></div>
            <div>
              <dt>Vehicles</dt>
              <dd>{{ detail.supportedVehicleTypes.join(', ') }}</dd>
            </div>
            <div>
              <dt>Coordinates</dt>
              <dd>{{ detail.latitude }}, {{ detail.longitude }}</dd>
            </div>
          </dl>

          <div>
            <h3>Availability</h3>
            <p class="muted small">
              Wall-clock hours in {{ detail.timeZoneId }}. A window ending before it starts runs
              overnight; equal start and end means the space is open 24 hours.
            </p>

            @if (detail.availabilityWindows.length === 0) {
              <p class="muted">No hours published — this space cannot be booked.</p>
            } @else {
              <ul class="windows">
                @for (window of detail.availabilityWindows; track $index) {
                  <li>
                    <strong>{{ dayName(window.dayOfWeek) }}</strong>
                    <span>{{ describe(window.startTime, window.endTime) }}</span>
                  </li>
                }
              </ul>
            }
          </div>
        </section>
      }
    </div>
  `,
  styles: [
    `
      .space-between {
        justify-content: space-between;
      }

      .small {
        font-size: 0.8rem;
      }

      dl {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
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
      }

      ul.windows {
        list-style: none;
        margin: 0;
        padding: 0;
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
        gap: var(--space-2);
      }

      ul.windows li {
        display: flex;
        justify-content: space-between;
        gap: var(--space-3);
        padding: var(--space-2) var(--space-3);
        border: 1px solid var(--border);
        border-radius: var(--radius);
      }
    `,
  ],
})
export class ListingsComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly listings = signal<ListingSummary[]>([]);
  readonly selected = signal<ListingDetail | null>(null);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.load();
  }

  select(spaceId: string): void {
    this.api.listing(spaceId).subscribe({
      next: (detail) => this.selected.set(detail),
      error: (err: Error) => this.error.set(err.message),
    });
  }

  publish(spaceId: string): void {
    this.mutate(() => this.api.publishListing(spaceId));
  }

  setStatus(spaceId: string, status: string): void {
    this.mutate(() => this.api.setListingStatus(spaceId, status));
  }

  dayName(day: number): string {
    return DAY_NAMES[day] ?? String(day);
  }

  /** "09:00:00" → "09:00", plus the two special cases the API's window model allows. */
  describe(start: string, end: string): string {
    const from = start.slice(0, 5);
    const to = end.slice(0, 5);

    if (from === to) {
      return 'Open 24 hours';
    }

    return end < start ? `${from} – ${to} (overnight)` : `${from} – ${to}`;
  }

  private mutate(action: () => { subscribe: (o: object) => unknown }): void {
    this.busy.set(true);
    this.error.set(null);

    action().subscribe({
      next: () => {
        this.busy.set(false);
        this.load();
      },
      error: (err: Error) => {
        this.busy.set(false);
        // Publishing legitimately fails when the price is outside the city band or no hours are
        // set, so the API's message is the useful thing to show.
        this.error.set(err.message);
      },
    });
  }

  private load(): void {
    this.loading.set(true);

    this.api.myListings().subscribe({
      next: (listings) => {
        this.listings.set(listings);
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }
}
