import { DecimalPipe } from '@angular/common';
import { Component, ElementRef, Input, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import * as L from 'leaflet';

import { ApiService } from '../../core/api.service';
import { BookingQuote, ListingDetail, Vehicle, Wallet } from '../../core/models';
import { DAY_NAMES } from '../../core/models';
import { SearchService, describeLocal, toLocalInput } from '../../shared/search.service';

/**
 * One space, and the panel that books it. Change the hours and the total and the wallet line
 * follow. The quote comes from the API so the number here is the number that will be held.
 */
@Component({
  selector: 'app-space',
  standalone: true,
  imports: [DecimalPipe, FormsModule, RouterLink],
  template: `
    <a routerLink="/explore" class="back">‹ Back to results</a>

    @if (error(); as message) {
      <div class="banner banner--error" role="alert">{{ message }}</div>
    }

    @if (listing(); as l) {
      <div class="gallery" [class.single]="l.photoUrls.length < 2">
        <div class="photo hero">
          @if (l.photoUrls[0]; as url) {
            <img [src]="url" alt="{{ l.summary.title }}" />
          } @else {
            <span class="photo__hint">no photo yet<br />the host can add one from their listing</span>
          }
        </div>
        @if (l.photoUrls.length > 1) {
          <div class="photo side">
            <img [src]="l.photoUrls[1]" alt="" />
          </div>
          <div class="photo side">
            @if (l.photoUrls[2]; as url) {
              <img [src]="url" alt="" />
            } @else {
              <span class="photo__hint">photo</span>
            }
          </div>
        }
      </div>

      <div class="columns">
        <div class="about">
          <h1>{{ l.summary.title }}</h1>
          <p class="where">{{ l.summary.addressLine }}, {{ l.summary.city }}</p>

          <div class="host">
            <div class="avatar">H</div>
            <div>
              <div class="host-name">Hosted on ParkNest <span class="badge badge--accent">✓ Verified host</span></div>
              <div class="muted small">Every host verifies their identity before they can be paid.</div>
            </div>
          </div>

          <h2>What's here</h2>
          <div class="amenities">
            @for (v of l.supportedVehicleTypes; track v) {
              <div><span class="ic"></span>Fits a {{ v === 'TwoWheeler' ? '2-wheeler' : '4-wheeler' }}</div>
            }
          </div>

          <!-- One line when it is the common case, a week otherwise. Seven tiles all saying
               "24 hours" told the renter nothing except that there were seven of them. -->
          <h2>When you can park</h2>
          @if (l.availabilityWindows.length === 0) {
            <p class="hours-note muted">The host has not published opening hours yet.</p>
          } @else if (alwaysOpen()) {
            <p class="hours-always"><span class="ic"></span>Open 24 hours, every day</p>
          } @else {
            <ul class="hours">
              @for (row of hoursByDay(); track row.day) {
                <li [class.closed]="!row.label">
                  <span class="day">{{ row.day }}</span>
                  <span>{{ row.label ?? 'Closed' }}</span>
                </li>
              }
            </ul>
          }

          <h2>Good to know</h2>
          <ul class="rules">
            <li>Free cancellation up to an hour before you arrive.</li>
            <li>Leave early and the unused hours come back to your balance.</li>
            @if (quote(); as q) {
              <li>Stay past your slot and the extra time is billed in 15-minute steps at ₹{{ q.overstayRatePerHour | number: '1.0-0' }}/hr.</li>
            }
            <li>Hours are the host's local time ({{ l.timeZoneId }}).</li>
          </ul>

          <h2>Where it is</h2>
          <div class="map-wrap">
            <div #map class="map"></div>
            <div class="map-note">Exact address after you book</div>
          </div>
        </div>

        <aside class="panel">
          <div class="rate">
            <span class="amount">₹{{ l.summary.pricePerHour | number: '1.0-0' }}</span>
            <span class="unit">per hour</span>
          </div>

          <div class="picker">
            <div class="two">
              <label class="cell">
                <span class="overline">From</span>
                <input type="datetime-local" name="from" [(ngModel)]="startLocal" (ngModelChange)="onTimeChange()" />
              </label>
              <div class="cell">
                <span class="overline">To</span>
                <div class="value">{{ endLabel() }}</div>
              </div>
            </div>
            <div class="hours-row">
              <span>{{ hoursLabel() }}</span>
              <div class="stepper">
                <button type="button" aria-label="One hour less" (click)="step(-1)" [disabled]="hoursValue() <= 1">−</button>
                <button type="button" aria-label="One hour more" (click)="step(1)" [disabled]="hoursValue() >= 12">+</button>
              </div>
            </div>
            <label class="cell vehicle">
              <span class="overline">Vehicle</span>
              @if (vehicles().length === 0) {
                <div class="value muted">No vehicle yet · <a routerLink="/vehicles">add your car</a></div>
              } @else {
                <select name="vehicle" [(ngModel)]="vehicleId">
                  @for (v of vehicles(); track v.id) {
                    <option [value]="v.id">{{ v.plateNumber }} · {{ v.type === 'TwoWheeler' ? '2-wheeler' : '4-wheeler' }}</option>
                  }
                </select>
              }
            </label>
          </div>

          <button type="button" class="primary lg block" [disabled]="!canReserve()" (click)="reserve()">
            @if (busy()) {
              Reserving…
            } @else if (vehicles().length === 0) {
              Add a vehicle to reserve
            } @else if (quote() && !quote()!.canBook) {
              Not available then
            } @else {
              Reserve
            }
          </button>

          @if (quote(); as q) {
            @if (q.canBook) {
              <div class="breakdown">
                <div class="line"><span>{{ hoursLabel() }} × ₹{{ q.ratePerHour | number: '1.0-0' }}</span><span>₹{{ q.amount | number: '1.0-0' }}</span></div>
                <div class="rule"></div>
                <div class="line total"><span>Reserved from your balance</span><span>₹{{ q.amount | number: '1.0-0' }}</span></div>
                <div class="wallet" [class.short]="shortfall() > 0">
                  {{ walletLine() }}
                  @if (shortfall() > 0) {
                    <a routerLink="/wallet">Add money</a>
                  }
                </div>
              </div>
            } @else {
              <div class="banner banner--warn">{{ q.unavailable }}</div>
            }
          }

          <p class="fine">Free cancellation up to an hour before you arrive. Leave early and the unused hours come back to your balance.</p>
        </aside>
      </div>
    } @else if (!error()) {
      <p class="muted">Loading the space…</p>
    }
  `,
  styles: [
    `
      :host {
        display: block;
      }

      .back {
        display: inline-block;
        margin-bottom: 18px;
        font-weight: 600;
        color: var(--ink-soft);
      }

      .gallery {
        display: grid;
        grid-template-columns: 2fr 1fr;
        grid-template-rows: 200px 200px;
        gap: 10px;
        margin-bottom: 32px;
      }

      .gallery.single {
        grid-template-columns: 1fr;
        grid-template-rows: 380px;
      }

      .hero {
        grid-row: span 2;
        border-radius: 20px 8px 8px 20px;
      }

      .single .hero {
        border-radius: 20px;
      }

      .side {
        border-radius: 8px;
      }

      .side:nth-child(2) {
        border-radius: 8px 20px 8px 8px;
      }

      .side:nth-child(3) {
        border-radius: 8px 8px 20px 8px;
      }

      .columns {
        display: grid;
        grid-template-columns: minmax(0, 1fr) 400px;
        gap: 56px;
        align-items: start;
      }

      .about h1 {
        font-size: clamp(26px, 3.5vw, 34px);
        margin-bottom: 10px;
      }

      .where {
        color: var(--ink-soft);
        font-weight: 500;
        margin-bottom: 22px;
      }

      .host {
        display: flex;
        align-items: center;
        gap: 14px;
        padding: 18px 0;
        border-top: 1px solid var(--hairline);
        border-bottom: 1px solid var(--hairline);
        margin-bottom: 26px;
      }

      .avatar {
        width: 48px;
        height: 48px;
        border-radius: 50%;
        background: var(--border);
        display: flex;
        align-items: center;
        justify-content: center;
        font: 700 16px/1 var(--font-body);
        color: var(--ink-soft);
        flex: 0 0 auto;
      }

      .host-name {
        display: flex;
        align-items: center;
        gap: 9px;
        flex-wrap: wrap;
        font-weight: 700;
      }

      .about h2 {
        font-size: 20px;
        margin: 0 0 16px;
      }

      .amenities {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 14px 28px;
        margin-bottom: 34px;
        max-width: 520px;
      }

      .amenities > div {
        display: flex;
        align-items: center;
        gap: 12px;
        font-weight: 500;
      }

      .hours-always {
        display: flex;
        align-items: center;
        gap: 12px;
        font-weight: 600;
        margin: 0 0 34px;
      }

      .hours-note {
        margin: 0 0 34px;
      }

      .hours {
        list-style: none;
        margin: 0 0 34px;
        padding: 0;
        max-width: 420px;
        border: 1px solid var(--border);
        border-radius: var(--r-card);
        overflow: hidden;
      }

      .hours li {
        display: flex;
        justify-content: space-between;
        gap: 16px;
        padding: 10px 14px;
        border-top: 1px solid var(--hairline);
        font-variant-numeric: tabular-nums;
      }

      .hours li:first-child {
        border-top: 0;
      }

      .hours .day {
        font-weight: 600;
      }

      .hours .closed {
        color: var(--ink-muted);
      }

      .ic {
        width: 32px;
        height: 32px;
        border-radius: 9px;
        background: var(--sunken);
        flex: 0 0 auto;
      }

      .rules {
        margin: 0 0 34px;
        padding-left: 20px;
        font-size: 16px;
        line-height: 1.7;
        color: var(--ink-soft);
        max-width: 56ch;
      }

      .map-wrap {
        position: relative;
        height: 280px;
        border-radius: 18px;
        overflow: hidden;
        background: #e8e2d8;
      }

      .map {
        height: 100%;
        z-index: 0;
      }

      .map-note {
        position: absolute;
        bottom: 12px;
        left: 12px;
        z-index: 1;
        background: rgb(255 255 255 / 92%);
        border-radius: 8px;
        padding: 6px 10px;
        font: 500 12px/1.3 var(--font-body);
        color: var(--ink-soft);
      }

      .panel {
        position: sticky;
        top: 96px;
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: 24px;
        padding: 26px;
        box-shadow: 0 8px 28px rgb(28 26 23 / 8%);
      }

      .rate {
        display: flex;
        align-items: baseline;
        gap: 5px;
        margin-bottom: 22px;
      }

      .amount {
        font: 600 32px/1 var(--font-display);
        letter-spacing: -0.025em;
      }

      .unit {
        font: 600 16px/1 var(--font-body);
        color: var(--ink-muted);
      }

      .picker {
        border: 1px solid var(--border);
        border-radius: 14px;
        overflow: hidden;
        margin-bottom: 14px;
      }

      .two {
        display: grid;
        grid-template-columns: 1fr 1fr;
      }

      .two .cell + .cell {
        border-left: 1px solid var(--hairline);
      }

      .cell {
        display: block;
        padding: 13px 16px;
        margin: 0;
      }

      .cell .overline {
        display: block;
        margin-bottom: 6px;
        font-size: 10px;
      }

      .cell input,
      .cell select {
        height: 24px;
        border: 0;
        padding: 0;
        border-radius: 0;
        background: transparent;
        font: 600 15px/1 var(--font-body);
      }

      .cell input:focus-visible,
      .cell select:focus-visible {
        box-shadow: none;
      }

      .value {
        font: 600 15px/1.3 var(--font-body);
      }

      .hours-row {
        border-top: 1px solid var(--hairline);
        padding: 11px 16px;
        display: flex;
        align-items: center;
        justify-content: space-between;
        font: 600 14px/1 var(--font-body);
        color: var(--ink-soft);
      }

      .stepper {
        display: flex;
        gap: 8px;
      }

      .stepper button {
        width: 34px;
        height: 34px;
        padding: 0;
        border-radius: 10px;
        font-size: 16px;
      }

      .vehicle {
        border-top: 1px solid var(--hairline);
      }

      .breakdown {
        margin-top: 20px;
        display: flex;
        flex-direction: column;
        gap: 11px;
      }

      .line {
        display: flex;
        justify-content: space-between;
        font: 500 15px/1.4 var(--font-body);
        color: var(--ink-soft);
      }

      .line.total {
        font-weight: 700;
        color: var(--ink);
      }

      .rule {
        height: 1px;
        background: var(--hairline);
      }

      .wallet {
        font: 500 13px/1.5 var(--font-body);
        color: var(--ink-muted);
      }

      .wallet.short {
        color: var(--danger-ink);
      }

      .fine {
        margin: 20px 0 0;
        font-size: 13px;
        line-height: 1.55;
        color: var(--ink-muted);
      }

      @media (max-width: 900px) {
        .gallery,
        .gallery.single {
          grid-template-columns: 1fr;
          grid-template-rows: auto;
          margin: -20px -16px 20px;
        }

        .hero {
          grid-row: auto;
          aspect-ratio: 4 / 3;
          border-radius: 0;
        }

        .side {
          display: none;
        }

        .columns {
          grid-template-columns: 1fr;
          gap: 24px;
        }

        .amenities {
          grid-template-columns: 1fr;
        }

        .panel {
          position: static;
          padding: 18px;
        }
      }
    `,
  ],
})
export class SpaceComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly search = inject(SearchService);

  /** Bound from the route by withComponentInputBinding. */
  @Input({ required: true }) spaceId!: string;

  readonly listing = signal<ListingDetail | null>(null);
  readonly vehicles = signal<Vehicle[]>([]);
  readonly wallet = signal<Wallet | null>(null);
  readonly quote = signal<BookingQuote | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  startLocal = this.search.state().startLocal;
  readonly hoursValue = signal(this.search.state().hours);
  vehicleId = '';

  readonly hoursLabel = computed(() => `${this.hoursValue()} ${this.hoursValue() === 1 ? 'hour' : 'hours'}`);
  readonly endLabel = computed(() => {
    const end = new Date(new Date(this.startLocal).getTime() + this.hoursValue() * 3_600_000);
    return describeLocal(toLocalInput(end));
  });
  readonly shortfall = computed(() => {
    const q = this.quote();
    const w = this.wallet();
    return q && w ? Math.max(0, q.amount - w.spendable) : 0;
  });
  readonly walletLine = computed(() => {
    const q = this.quote();
    const w = this.wallet();
    if (!q || !w) {
      return '';
    }
    const short = this.shortfall();
    return short > 0
      ? `Your balance is ₹${fmt(w.spendable)} — ₹${fmt(short)} short.`
      : `Balance ₹${fmt(w.spendable)} · ₹${fmt(q.amount)} will be set aside, ₹${fmt(w.spendable - q.amount)} left over.`;
  });
  @ViewChild('map') private mapEl?: ElementRef<HTMLDivElement>;
  private map?: L.Map;

  /** A method, not a computed: vehicleId is a plain ngModel field. */
  canReserve(): boolean {
    return !this.busy() && this.vehicles().length > 0 && !!this.vehicleId && !!this.quote()?.canBook && this.shortfall() === 0;
  }

  ngOnInit(): void {
    forkJoin({
      listing: this.api.listing(this.spaceId),
      vehicles: this.api.myVehicles(),
      wallet: this.api.myWallet(),
    }).subscribe({
      next: ({ listing, vehicles, wallet }) => {
        this.listing.set(listing);
        this.vehicles.set(vehicles);
        this.vehicleId = vehicles[0]?.id ?? '';
        this.wallet.set(wallet);
        this.refreshQuote();
        setTimeout(() => this.drawMap(listing), 0);
      },
      error: (err: Error) => this.error.set(err.message),
    });
  }

  step(delta: number): void {
    this.hoursValue.set(Math.min(12, Math.max(1, this.hoursValue() + delta)));
    this.search.update({ hours: this.hoursValue() });
    this.refreshQuote();
  }

  onTimeChange(): void {
    this.search.update({ startLocal: this.startLocal });
    this.refreshQuote();
  }

  dayName(day: number): string {
    return DAY_NAMES[day]?.slice(0, 3) ?? '';
  }

  hours(start: string, end: string): string {
    const from = start.slice(0, 5);
    const to = end.slice(0, 5);
    return from === to ? 'All day' : `${from}–${to}`;
  }

  /** The week as seven rows, Monday first, with the days the space is shut said so. */
  readonly hoursByDay = computed(() => {
    const l = this.listing();

    if (!l) {
      return [];
    }

    // Monday first: that is how people read a week, whatever the enum starts on.
    return [1, 2, 3, 4, 5, 6, 0].map((day) => {
      // The API writes enums as their names ("Monday"), so match on the name; the number is
      // accepted too in case that ever changes.
      const windows = l.availabilityWindows.filter(
        (w) => w.dayOfWeek === day || String(w.dayOfWeek) === DAY_NAMES[day],
      );

      return {
        day: DAY_NAMES[day],
        label: windows.length === 0 ? null : windows.map((w) => this.hours(w.startTime, w.endTime)).join(', '),
      };
    });
  });

  readonly alwaysOpen = computed(() => {
    const rows = this.hoursByDay();
    return rows.length === 7 && rows.every((r) => r.label === 'All day');
  });

  reserve(): void {
    const l = this.listing();
    if (!l || !this.canReserve()) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    const start = new Date(this.startLocal).toISOString();
    const minutes = this.hoursValue() * 60;

    this.api
      .book(l.summary.id, this.vehicleId, start, minutes, `book:${l.summary.id}:${this.startLocal}:${minutes}`)
      .subscribe({
        next: (booking) => {
          this.busy.set(false);
          void this.router.navigate(['/bookings', booking.id], { queryParams: { booked: 1 } });
        },
        error: (err: Error) => {
          this.busy.set(false);
          this.error.set(err.message);
        },
      });
  }

  private refreshQuote(): void {
    const l = this.listing();
    if (!l || Number.isNaN(new Date(this.startLocal).getTime())) {
      return;
    }
    this.api.quote(l.summary.id, new Date(this.startLocal).toISOString(), this.hoursValue() * 60).subscribe({
      next: (q) => this.quote.set(q),
      error: (err: Error) => {
        this.quote.set(null);
        this.error.set(err.message);
      },
    });
  }

  private drawMap(listing: ListingDetail): void {
    const host = this.mapEl?.nativeElement;
    if (!host || this.map) {
      return;
    }
    this.map = L.map(host, {
      center: [listing.latitude, listing.longitude],
      zoom: 15,
      zoomControl: false,
      dragging: false,
      scrollWheelZoom: false,
      attributionControl: true,
    });
    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19,
      attribution: '&copy; OpenStreetMap contributors',
    }).addTo(this.map);
    // A circle, not a pin: the exact address is for people who have booked.
    L.circle([listing.latitude, listing.longitude], {
      radius: 180,
      color: '#c8622f',
      weight: 2,
      fillColor: '#c8622f',
      fillOpacity: 0.18,
    }).addTo(this.map);
    setTimeout(() => this.map?.invalidateSize(), 0);
  }
}

function fmt(n: number): string {
  return Math.round(n).toLocaleString('en-IN');
}
