import { CurrencyPipe, DatePipe, DecimalPipe } from '@angular/common';
import { Component, OnInit, ViewChild, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { ApiService } from '../../core/api.service';
import { LocationService } from '../../shared/location.service';
import { MapPickerComponent } from '../../shared/map-picker.component';
import { BookingQuote, NearbySpace, Vehicle } from '../../core/models';

@Component({
  selector: 'app-explore',
  standalone: true,
  imports: [CurrencyPipe, DatePipe, DecimalPipe, FormsModule, RouterLink, MapPickerComponent],
  template: `
    <div class="stack">
      <div>
        <h1>Find parking</h1>
        <p class="muted">Spaces near a point, nearest first. Distances come from PostGIS.</p>
      </div>

      @if (error(); as message) {
        <div class="banner banner--error" role="alert">{{ message }}</div>
      }

      @if (vehicles().length === 0 && !loading()) {
        <div class="banner banner--info">
          You have no vehicles registered, so you cannot book yet.
          <a routerLink="/vehicles">Add one first</a>.
        </div>
      }

      <section class="card stack">
        <div class="row">
          <button type="button" class="primary" [disabled]="locating()" (click)="useMyLocation()">
            {{ locating() ? 'Locating…' : 'Use my location' }}
          </button>
          <span class="muted small">
            Or drag the pin. Coordinates below update either way.
          </span>
        </div>

        <app-map-picker
          #picker
          [latitude]="lat"
          [longitude]="lng"
          [zoom]="14"
          [height]="260"
          (pointChanged)="onPointChanged($event)"
        />

        <form class="grid" (ngSubmit)="search()">
          <div>
            <label for="lat">Latitude</label>
            <input id="lat" name="lat" type="number" step="0.0001" [(ngModel)]="lat" />
          </div>
          <div>
            <label for="lng">Longitude</label>
            <input id="lng" name="lng" type="number" step="0.0001" [(ngModel)]="lng" />
          </div>
          <div>
            <label for="radius">Radius (m)</label>
            <input id="radius" name="radius" type="number" step="500" [(ngModel)]="radius" />
          </div>
          <div>
            <label for="maxPrice">Max price/hr</label>
            <input id="maxPrice" name="maxPrice" type="number" step="10" [(ngModel)]="maxPrice" />
          </div>
          <div class="actions">
            <button class="primary" type="submit" [disabled]="loading()">Search</button>
          </div>
        </form>
      </section>

      @if (loading()) {
        <p class="muted">Searching…</p>
      } @else if (searched() && results().length === 0) {
        <p class="muted">Nothing published within that radius.</p>
      } @else if (results().length > 0) {
        <section class="results">
          @for (space of results(); track space.id) {
            <div class="card space" [class.space--picked]="picked()?.id === space.id">
              <div>
                <strong>{{ space.title }}</strong>
                <div class="muted small">{{ space.addressLine }}</div>
              </div>
              <div class="row space-between">
                <span>{{ space.pricePerHour | currency: 'INR' : 'symbol' : '1.2-2' }}/hr</span>
                <span class="muted small">{{ space.distanceMetres | number: '1.0-0' }} m</span>
              </div>
              <button type="button" (click)="pick(space)">Select</button>
            </div>
          }
        </section>
      }

      @if (picked(); as space) {
        <section class="card stack">
          <h2>Book {{ space.title }}</h2>

          <div class="grid">
            <div>
              <label for="start">Start</label>
              <input id="start" name="start" type="datetime-local" [(ngModel)]="startLocal" (ngModelChange)="refreshQuote()" />
            </div>
            <div>
              <label for="duration">Duration (minutes)</label>
              <input
                id="duration"
                name="duration"
                type="number"
                min="30"
                step="15"
                [(ngModel)]="durationMinutes"
                (ngModelChange)="refreshQuote()"
              />
            </div>
            <div>
              <label for="vehicle">Vehicle</label>
              <select id="vehicle" name="vehicle" [(ngModel)]="vehicleId">
                @for (v of vehicles(); track v.id) {
                  <option [value]="v.id">{{ v.plateNumber }}</option>
                }
              </select>
            </div>
          </div>

          @if (quote(); as q) {
            <!-- The quote reserves nothing; it exists so the renter sees the cost and any
                 obstacle before credits move. -->
            <div class="quote" [class.quote--blocked]="!q.canBook">
              @if (q.canBook) {
                <div><strong>{{ q.amount | currency: 'INR' : 'symbol' : '1.2-2' }}</strong> for {{ q.billedMinutes }} minutes</div>
                <div class="muted small">
                  {{ q.startTime | date: 'short' }} → {{ q.endTime | date: 'short' }} ·
                  overstay billed at {{ q.overstayRatePerHour | currency: 'INR' : 'symbol' : '1.2-2' }}/hr
                </div>
              } @else {
                <div><strong>Not available.</strong> {{ q.unavailable }}</div>
              }
            </div>
          }

          <div class="row">
            <button
              class="primary"
              type="button"
              [disabled]="busy() || !quote()?.canBook || !vehicleId"
              (click)="book()"
            >
              Reserve credits and book
            </button>
            <button type="button" (click)="picked.set(null)">Cancel</button>
          </div>

          <p class="muted small">
            Credits are held the moment you book — that is what makes the overstay bill itself
            later, with nothing to collect from you afterwards.
          </p>
        </section>
      }
    </div>
  `,
  styles: [
    `
      .grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
        gap: var(--space-4);
        align-items: end;
      }

      .actions {
        display: flex;
      }

      .results {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
        gap: var(--space-4);
      }

      .space {
        display: flex;
        flex-direction: column;
        gap: var(--space-3);
      }

      .space--picked {
        border-color: var(--accent);
      }

      .space-between {
        justify-content: space-between;
      }

      .small {
        font-size: 0.8rem;
      }

      .quote {
        padding: var(--space-3) var(--space-4);
        border: 1px solid var(--positive);
        border-radius: var(--radius);
        color: var(--positive);
      }

      .quote--blocked {
        border-color: var(--negative);
        color: var(--negative);
      }
    `,
  ],
})
export class ExploreComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly location = inject(LocationService);

  @ViewChild('picker') picker?: MapPickerComponent;

  lat = 12.9716;
  lng = 77.5946;
  radius = 5000;
  maxPrice: number | null = null;

  startLocal = toLocalInput(new Date(Date.now() + 10 * 60_000));
  durationMinutes = 60;
  vehicleId = '';

  readonly results = signal<NearbySpace[]>([]);
  readonly picked = signal<NearbySpace | null>(null);
  readonly quote = signal<BookingQuote | null>(null);
  readonly vehicles = signal<Vehicle[]>([]);
  readonly loading = signal(false);
  readonly searched = signal(false);
  readonly busy = signal(false);
  readonly locating = signal(false);
  readonly error = signal<string | null>(null);

  async useMyLocation(): Promise<void> {
    this.locating.set(true);
    this.error.set(null);

    try {
      const position = await this.location.current();
      this.lat = round6(position.latitude);
      this.lng = round6(position.longitude);
      this.picker?.moveTo(this.lat, this.lng);
      this.search();
    } catch (err) {
      // Permission denied is a normal outcome, not a crash — keep the typed coordinates usable.
      this.error.set((err as Error).message);
    } finally {
      this.locating.set(false);
    }
  }

  onPointChanged(point: { latitude: number; longitude: number }): void {
    this.lat = round6(point.latitude);
    this.lng = round6(point.longitude);
    this.search();
  }

  ngOnInit(): void {
    this.api.myVehicles().subscribe({
      next: (v) => {
        this.vehicles.set(v);
        this.vehicleId = v[0]?.id ?? '';
      },
      error: (err: Error) => this.error.set(err.message),
    });

    this.search();
  }

  search(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.searchNearby(this.lat, this.lng, this.radius, this.maxPrice ?? undefined).subscribe({
      next: (spaces) => {
        this.results.set(spaces);
        this.loading.set(false);
        this.searched.set(true);
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.loading.set(false);
        this.searched.set(true);
      },
    });
  }

  pick(space: NearbySpace): void {
    this.picked.set(space);
    this.refreshQuote();
  }

  refreshQuote(): void {
    const space = this.picked();
    if (!space) {
      return;
    }

    this.api.quote(space.id, new Date(this.startLocal).toISOString(), this.durationMinutes).subscribe({
      next: (q) => this.quote.set(q),
      error: (err: Error) => {
        this.quote.set(null);
        this.error.set(err.message);
      },
    });
  }

  book(): void {
    const space = this.picked();
    if (!space) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    this.api
      .book(
        space.id,
        this.vehicleId,
        new Date(this.startLocal).toISOString(),
        this.durationMinutes,
        // Stable per attempt, so a double-tap or a retry cannot create two bookings.
        `book:${space.id}:${this.startLocal}:${this.durationMinutes}`,
      )
      .subscribe({
        next: (booking) => {
          this.busy.set(false);
          void this.router.navigate(['/bookings', booking.id]);
        },
        error: (err: Error) => {
          this.busy.set(false);
          this.error.set(err.message);
        },
      });
  }
}

/** Six decimals is roughly 0.1 m — more precision than a phone GPS can justify. */
function round6(value: number): number {
  return Math.round(value * 1e6) / 1e6;
}

/** datetime-local wants "YYYY-MM-DDTHH:mm" in local time, not an ISO string. */
function toLocalInput(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return (
    date.getFullYear() +
    '-' + pad(date.getMonth() + 1) +
    '-' + pad(date.getDate()) +
    'T' + pad(date.getHours()) +
    ':' + pad(date.getMinutes())
  );
}
