import { DecimalPipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';

import { ApiService } from '../../core/api.service';
import { BookingSummary, DAY_NAMES, ListingDetail, ListingPhoto, ListingSummary, Wallet } from '../../core/models';
import { describeLocal } from '../../shared/search.service';

/**
 * Hosting. Your spaces as cards with the photo first, because a listing without a photo is the
 * single biggest reason a renter scrolls past; the upload lives right on the card.
 */
@Component({
  selector: 'app-listings',
  standalone: true,
  imports: [DecimalPipe, RouterLink],
  template: `
    <div class="page-head">
      <div>
        <h1>Your spaces</h1>
        <p class="lede">Publish a space and it shows up in search straight away.</p>
      </div>
      <a routerLink="/listings/new" class="btn primary">List a space</a>
    </div>

    @if (error(); as message) {
      <div class="banner banner--error" role="alert">{{ message }}</div>
    }

    @if (loading()) {
      <p class="muted">Loading…</p>
    } @else {
      <section class="stats">
        <div class="stat">
          <div class="overline">Earned, ready to withdraw</div>
          <div class="num">₹{{ wallet()?.earning ?? 0 | number: '1.0-0' }}</div>
          <a routerLink="/payouts" class="small">Withdraw</a>
        </div>
        <div class="stat">
          <div class="overline">Live spaces</div>
          <div class="num">{{ liveCount() }}</div>
          <span class="muted small">of {{ listings().length }}</span>
        </div>
        <div class="stat">
          <div class="overline">Arrivals today</div>
          <div class="num">{{ arrivals().length }}</div>
          <span class="muted small">bookings starting today</span>
        </div>
      </section>

      @if (arrivals().length > 0) {
        <section class="arrivals">
          <h2>Today's arrivals</h2>
          @for (b of arrivals(); track b.id) {
            <a class="arrival" [routerLink]="['/bookings', b.id]">
              <div>
                <div class="strong">{{ b.spaceTitle }}</div>
                <div class="muted small">{{ describeLocal(b.startTime) }}</div>
              </div>
              <span class="badge" [class.badge--live]="b.status === 'Active'">{{ b.status === 'Active' ? 'Parked now' : 'Reserved' }}</span>
            </a>
          }
        </section>
      }

      @if (listings().length === 0) {
        <div class="empty">
          <div class="empty__mark"></div>
          <h3>You're not hosting anything yet</h3>
          <p>A driveway that sits empty on weekdays can earn a few thousand rupees a month.</p>
          <a routerLink="/listings/new" class="btn primary">List your space</a>
        </div>
      } @else {
        <div class="grid">
          @for (l of listings(); track l.id) {
            <article class="space">
              <div class="photo cover">
                @if (photoOf(l.id); as p) {
                  <img [src]="p.url" alt="" />
                } @else {
                  <span class="photo__hint">no photo yet</span>
                }
                <span class="badge status" [class]="'badge status ' + badgeClass(l.status)">{{ statusLabel(l.status) }}</span>
                <label class="add-photo">
                  <input type="file" accept="image/*" (change)="upload(l.id, $event)" [disabled]="busy()" />
                  {{ photoOf(l.id) ? 'Change photo' : 'Add photo' }}
                </label>
              </div>
              <div class="body">
                <div class="top">
                  <div>
                    <h3>{{ l.title }}</h3>
                    <div class="muted small">{{ l.addressLine }}, {{ l.city }}</div>
                  </div>
                  <div class="rate"><span class="amount">₹{{ l.pricePerHour | number: '1.0-0' }}</span><span class="unit">/hr</span></div>
                </div>
                <div class="muted small">{{ l.activeBookings }} {{ l.activeBookings === 1 ? 'booking' : 'bookings' }} right now</div>
                <div class="actions">
                  @if (l.status === 'Draft' || l.status === 'Paused') {
                    <button type="button" class="primary sm" (click)="publish(l.id)" [disabled]="busy()">
                      {{ l.status === 'Draft' ? 'Publish' : 'Resume' }}
                    </button>
                  } @else if (l.status === 'Published') {
                    <button type="button" class="sm" (click)="setStatus(l.id, 'Paused')" [disabled]="busy()">Pause</button>
                  }
                  <button type="button" class="sm" (click)="toggle(l.id)">{{ selected()?.summary?.id === l.id ? 'Hide hours' : 'Hours' }}</button>
                  @if (l.status === 'Published') {
                    <a class="btn sm" [routerLink]="['/spaces', l.id]">View as renter</a>
                  }
                </div>

                @if (selected()?.summary?.id === l.id) {
                  <div class="hours">
                    @if (selected()!.availabilityWindows.length === 0) {
                      <p class="muted small">No hours published — this space cannot be booked yet.</p>
                    } @else {
                      @for (w of selected()!.availabilityWindows; track $index) {
                        <div class="window"><strong>{{ dayName(w.dayOfWeek) }}</strong><span>{{ describe(w.startTime, w.endTime) }}</span></div>
                      }
                    }
                    <p class="muted small">Hours are in {{ selected()!.timeZoneId }}.</p>
                  </div>
                }
              </div>
            </article>
          }
        </div>
      }
    }
  `,
  styles: [
    `
      .stats {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
        gap: 12px;
        margin-bottom: 28px;
      }

      .stat {
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: 16px;
        padding: 18px 20px;
      }

      .num {
        font: 600 30px/1.1 var(--font-display);
        letter-spacing: -0.025em;
        margin: 8px 0 4px;
      }

      .arrivals {
        margin-bottom: 28px;
      }

      .arrivals h2 {
        font-size: 20px;
        margin-bottom: 12px;
      }

      .arrival {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 12px;
        padding: 12px 16px;
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: 14px;
        color: var(--ink);
        margin-bottom: 8px;
      }

      .arrival:hover {
        text-decoration: none;
        border-color: var(--border-strong);
      }

      .strong {
        font-weight: 700;
      }

      .grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
        gap: 16px;
      }

      .space {
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: 18px;
        overflow: hidden;
      }

      .cover {
        aspect-ratio: 4 / 3;
      }

      .status {
        position: absolute;
        top: 12px;
        left: 12px;
      }

      .add-photo {
        position: absolute;
        right: 12px;
        bottom: 12px;
        margin: 0;
        height: 36px;
        padding: 0 14px;
        display: inline-flex;
        align-items: center;
        border-radius: 999px;
        background: rgb(255 255 255 / 94%);
        color: var(--ink);
        font: 700 13px/1 var(--font-body);
        cursor: pointer;
        box-shadow: 0 2px 8px rgb(28 26 23 / 14%);
      }

      .add-photo input {
        position: absolute;
        inset: 0;
        opacity: 0;
        cursor: pointer;
        height: 100%;
      }

      .body {
        padding: 16px;
      }

      .top {
        display: flex;
        justify-content: space-between;
        gap: 12px;
        align-items: flex-start;
        margin-bottom: 8px;
      }

      .top h3 {
        margin: 0 0 2px;
      }

      .rate {
        white-space: nowrap;
      }

      .amount {
        font: 600 20px/1 var(--font-display);
        letter-spacing: -0.02em;
      }

      .unit {
        font: 600 13px/1 var(--font-body);
        color: var(--ink-muted);
      }

      .actions {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
        margin-top: 14px;
      }

      .hours {
        margin-top: 14px;
        padding-top: 14px;
        border-top: 1px solid var(--hairline);
        display: flex;
        flex-direction: column;
        gap: 6px;
      }

      .window {
        display: flex;
        justify-content: space-between;
        font-size: 14px;
      }
    `,
  ],
})
export class ListingsComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly listings = signal<ListingSummary[]>([]);
  readonly wallet = signal<Wallet | null>(null);
  readonly arrivals = signal<BookingSummary[]>([]);
  readonly photos = signal<Record<string, ListingPhoto | undefined>>({});
  readonly selected = signal<ListingDetail | null>(null);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  readonly describeLocal = describeLocal;

  ngOnInit(): void {
    this.load();
  }

  liveCount(): number {
    return this.listings().filter((l) => l.status === 'Published').length;
  }

  photoOf(id: string): ListingPhoto | undefined {
    return this.photos()[id];
  }

  toggle(spaceId: string): void {
    if (this.selected()?.summary.id === spaceId) {
      this.selected.set(null);
      return;
    }
    this.api.listing(spaceId).subscribe({
      next: (detail) => this.selected.set(detail),
      error: (err: Error) => this.error.set(err.message),
    });
  }

  upload(spaceId: string, event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.api.addListingPhoto(spaceId, file).subscribe({
      next: (photo) => {
        this.busy.set(false);
        this.photos.set({ ...this.photos(), [spaceId]: photo });
      },
      error: (err: Error) => {
        this.busy.set(false);
        this.error.set(err.message);
      },
    });
  }

  publish(spaceId: string): void {
    this.mutate(() => this.api.publishListing(spaceId));
  }

  setStatus(spaceId: string, status: string): void {
    this.mutate(() => this.api.setListingStatus(spaceId, status));
  }

  statusLabel(status: string): string {
    return status === 'Published' ? 'Live' : status;
  }

  badgeClass(status: string): string {
    switch (status) {
      case 'Published':
        return 'badge--live';
      case 'Paused':
        return 'badge--warn';
      case 'Draft':
        return 'badge--draft';
      default:
        return 'badge--danger';
    }
  }

  /** The API writes the enum as its name ("Monday"); a number is accepted in case that changes. */
  dayName(day: number | string): string {
    return typeof day === 'number' ? DAY_NAMES[day] ?? String(day) : String(day);
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

    forkJoin({
      listings: this.api.myListings(),
      wallet: this.api.myWallet(),
      hosting: this.api.hostingBookings(),
    }).subscribe({
      next: ({ listings, wallet, hosting }) => {
        this.listings.set(listings);
        this.wallet.set(wallet);
        const today = new Date().toDateString();
        this.arrivals.set(
          hosting.filter(
            (b) => (b.status === 'Held' || b.status === 'Active') && new Date(b.startTime).toDateString() === today,
          ),
        );
        this.loading.set(false);
        this.loadPhotos(listings);
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }

  /** One call per space for the cover photo. Hosts have a handful of spaces, not hundreds. */
  private loadPhotos(listings: ListingSummary[]): void {
    for (const l of listings) {
      this.api.listingPhotos(l.id).subscribe({
        next: (photos) => this.photos.set({ ...this.photos(), [l.id]: photos[0] }),
        error: () => undefined,
      });
    }
  }
}
