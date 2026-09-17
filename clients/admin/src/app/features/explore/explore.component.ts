import { DecimalPipe } from '@angular/common';
import { Component, OnInit, ViewChild, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';

import { ApiService } from '../../core/api.service';
import { NearbySpace, VehicleType } from '../../core/models';
import { LocationService } from '../../shared/location.service';
import { ResultsMapComponent } from '../../shared/results-map.component';
import {
  CITIES,
  SearchService,
  describeDistance,
  describeLocal,
  toLocalInput,
} from '../../shared/search.service';

interface PriceOption {
  label: string;
  max: number | null;
}

const PRICE_OPTIONS: PriceOption[] = [
  { label: 'Price', max: null },
  { label: 'Under ₹40/hr', max: 40 },
  { label: 'Under ₹60/hr', max: 60 },
  { label: 'Under ₹100/hr', max: 100 },
];

const VEHICLE_OPTIONS: Array<{ label: string; type: VehicleType | null }> = [
  { label: 'Vehicle type', type: null },
  { label: '4-wheeler', type: 'FourWheeler' },
  { label: '2-wheeler', type: 'TwoWheeler' },
];

/**
 * Results and map. The renter searches by place and time; the coordinates behind that come from
 * the geocoder or the phone, never from a field. Hovering a card lights its pin.
 */
@Component({
  selector: 'app-explore',
  standalone: true,
  imports: [DecimalPipe, FormsModule, ResultsMapComponent],
  template: `
    <div class="bar">
      <form class="where" (ngSubmit)="applySearch()">
        <input class="area" name="area" placeholder="Area, e.g. Indiranagar" autocomplete="off" [(ngModel)]="area" />
        <select class="city" name="city" [(ngModel)]="city" aria-label="City">
          @for (c of cities; track c) {
            <option [value]="c">{{ c }}</option>
          }
        </select>
        <span class="sep"></span>
        <input class="when" name="from" type="datetime-local" [(ngModel)]="startLocal" aria-label="From" />
        <select class="hours" name="hours" [(ngModel)]="hours" aria-label="For how long">
          @for (h of hourOptions; track h) {
            <option [ngValue]="h">{{ h }}h</option>
          }
        </select>
        <button type="submit" class="go" aria-label="Search">→</button>
      </form>
      <button type="button" class="sm locate" [disabled]="locating()" (click)="useMyLocation()">
        {{ locating() ? 'Locating…' : 'Use my location' }}
      </button>
    </div>

    <div class="chips">
      <button type="button" class="chip" [class.selected]="availableNow()" (click)="toggleNow()">Available now</button>
      <button type="button" class="chip" [class.selected]="priceIndex() > 0" (click)="cyclePrice()">{{ priceLabel() }}</button>
      <button type="button" class="chip" [class.selected]="vehicleIndex() > 0" (click)="cycleVehicle()">{{ vehicleLabel() }}</button>
    </div>

    @if (error(); as message) {
      <div class="banner banner--error" role="alert">{{ message }}</div>
    }

    <div class="split" [class.show-map]="mapOpen()">
      <section class="list">
        <div class="list-head">
          <h2>
            @if (loading()) {
              Looking around {{ placeLabel() }}…
            } @else {
              {{ results().length }} {{ results().length === 1 ? 'space' : 'spaces' }} near {{ placeLabel() }}
            }
          </h2>
          <span class="muted small">Sorted by distance · {{ whenLabel() }}</span>
        </div>

        @if (!loading() && searched() && results().length === 0) {
          <div class="empty">
            <div class="empty__mark"></div>
            <h3>Nothing listed here yet</h3>
            <p>Try a nearby area, widen the time, or be the first to list a space around {{ placeLabel() }}.</p>
            <button type="button" class="primary" (click)="widen()">Search a wider area</button>
          </div>
        }

        @for (space of results(); track space.id) {
          <article
            class="result"
            [class.is-hot]="hovered() === space.id"
            tabindex="0"
            role="link"
            (mouseenter)="hovered.set(space.id)"
            (mouseleave)="hovered.set(null)"
            (click)="open(space)"
            (keydown.enter)="open(space)"
          >
            <div class="photo thumb">
              @if (space.photoUrl) {
                <img [src]="space.photoUrl" alt="" loading="lazy" />
              } @else {
                <span class="photo__hint">space photo</span>
              }
            </div>
            <div class="body">
              <div class="top">
                <div>
                  <h3>{{ space.title }}</h3>
                  <p class="meta">{{ space.addressLine }} · {{ distance(space) }}</p>
                </div>
                <div class="rate"><span class="amount">₹{{ space.pricePerHour | number: '1.0-0' }}</span><span class="unit">/hr</span></div>
              </div>
              <div class="foot">
                <span class="badge badge--live">Bookable</span>
                <span class="muted small">{{ space.city }}</span>
              </div>
            </div>
          </article>
        }
      </section>

      <aside class="map">
        <app-results-map
          #map
          [spaces]="results()"
          [center]="center()"
          [hoveredId]="hovered()"
          [selectedId]="null"
          (pinClicked)="open($event)"
        />
      </aside>
    </div>

    <button type="button" class="map-toggle" (click)="toggleMap()">
      <span class="dot"></span>{{ mapOpen() ? 'List' : 'Map' }}
    </button>
  `,
  styles: [
    `
      :host {
        display: block;
      }

      .bar {
        display: flex;
        gap: 10px;
        align-items: center;
        flex-wrap: wrap;
        margin-bottom: 14px;
      }

      .where {
        flex: 1 1 520px;
        display: flex;
        align-items: center;
        gap: 4px;
        height: 52px;
        padding: 0 6px 0 16px;
        border: 1px solid var(--border);
        border-radius: 999px;
        background: var(--surface);
        box-shadow: var(--shadow-sm);
      }

      .where input,
      .where select {
        height: 40px;
        border: 0;
        padding: 0 8px;
        background: transparent;
        font: 600 14px/1 var(--font-body);
        width: auto;
        min-width: 0;
      }

      .where input:focus-visible,
      .where select:focus-visible {
        box-shadow: none;
        background: var(--accent-50);
        border-radius: 8px;
      }

      .area {
        flex: 1 1 160px;
      }

      .city {
        flex: 0 0 auto;
        color: var(--ink-muted);
      }

      .sep {
        width: 1px;
        height: 18px;
        background: var(--border);
        margin: 0 4px;
      }

      .when {
        flex: 0 1 190px;
        color: var(--ink-soft);
      }

      .hours {
        flex: 0 0 auto;
        color: var(--ink-soft);
      }

      .go {
        width: 36px;
        height: 36px;
        padding: 0;
        border-radius: 50%;
        border: 0;
        background: var(--accent);
        color: #fff;
        flex: 0 0 auto;
      }

      .go:hover:not(:disabled) {
        background: var(--accent-600);
        border: 0;
      }

      .locate {
        border-radius: 999px;
      }

      .chips {
        display: flex;
        gap: 10px;
        flex-wrap: wrap;
        padding-bottom: 14px;
        border-bottom: 1px solid var(--hairline);
        margin-bottom: 18px;
      }

      .split {
        display: grid;
        grid-template-columns: minmax(0, 1fr) minmax(320px, 42%);
        gap: 24px;
        align-items: start;
      }

      .list {
        display: flex;
        flex-direction: column;
        gap: 14px;
        min-width: 0;
      }

      .list-head {
        display: flex;
        justify-content: space-between;
        align-items: baseline;
        gap: 12px;
        flex-wrap: wrap;
      }

      .list-head h2 {
        margin: 0;
        font-size: 21px;
      }

      .result {
        display: flex;
        gap: 16px;
        padding: 14px;
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: 18px;
        cursor: pointer;
        transition: box-shadow 0.18s, border-color 0.18s;
      }

      .result:hover,
      .result:focus-visible,
      .result.is-hot {
        box-shadow: 0 6px 20px rgb(28 26 23 / 10%);
        border-color: var(--border-strong);
        outline: none;
      }

      .thumb {
        width: 172px;
        height: 130px;
        border-radius: 14px;
        flex: 0 0 auto;
      }

      .body {
        flex: 1 1 auto;
        min-width: 0;
        display: flex;
        flex-direction: column;
      }

      .top {
        display: flex;
        justify-content: space-between;
        gap: 14px;
        align-items: flex-start;
      }

      .top h3 {
        margin: 0;
      }

      .meta {
        margin: 5px 0 0;
        font: 500 13px/1.4 var(--font-body);
        color: var(--ink-muted);
      }

      .rate {
        white-space: nowrap;
      }

      .amount {
        font: 600 21px/1 var(--font-display);
        letter-spacing: -0.02em;
      }

      .unit {
        font: 600 13px/1 var(--font-body);
        color: var(--ink-muted);
      }

      .foot {
        margin-top: auto;
        padding-top: 12px;
        display: flex;
        gap: 10px;
        align-items: center;
      }

      .map {
        position: sticky;
        top: 88px;
        height: calc(100vh - 112px);
        min-height: 420px;
        border-radius: 20px;
        overflow: hidden;
        border: 1px solid var(--border);
      }

      .map-toggle {
        display: none;
      }

      @media (max-width: 900px) {
        .split {
          grid-template-columns: 1fr;
        }

        .map {
          display: none;
          position: fixed;
          inset: 60px 0 calc(62px + env(safe-area-inset-bottom));
          height: auto;
          min-height: 0;
          border-radius: 0;
          border: 0;
          z-index: 15;
        }

        .split.show-map .map {
          display: block;
        }

        .map-toggle {
          position: fixed;
          left: 50%;
          bottom: calc(80px + env(safe-area-inset-bottom));
          transform: translateX(-50%);
          z-index: 16;
          display: inline-flex;
          height: 46px;
          padding: 0 22px;
          border: 0;
          border-radius: 999px;
          background: var(--ink);
          color: #fff;
          font: 700 14px/1 var(--font-body);
          box-shadow: 0 8px 24px rgb(28 26 23 / 28%);
        }

        .map-toggle:hover {
          background: var(--ink);
          border: 0;
          color: #fff;
        }

        .dot {
          width: 8px;
          height: 8px;
          border-radius: 2px;
          background: var(--accent-300);
        }

        .where {
          flex-basis: 100%;
          height: auto;
          flex-wrap: wrap;
          padding: 8px 10px;
          border-radius: 20px;
        }

        .area {
          flex: 1 1 60%;
        }

        .sep {
          display: none;
        }

        .when {
          flex: 1 1 60%;
        }

        .result {
          flex-direction: column;
          padding: 0;
          overflow: hidden;
        }

        .thumb {
          width: 100%;
          height: auto;
          aspect-ratio: 16 / 10;
          border-radius: 0;
        }

        .body {
          padding: 14px 15px 16px;
        }
      }
    `,
  ],
})
export class ExploreComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly location = inject(LocationService);
  private readonly search = inject(SearchService);

  @ViewChild('map') mapView?: ResultsMapComponent;

  readonly cities = CITIES;
  readonly hourOptions = [1, 2, 3, 4, 5, 6, 8, 10, 12];

  city = this.search.state().city;
  area = this.search.state().area;
  startLocal = this.search.state().startLocal;
  hours = this.search.state().hours;

  private radius = 3000;

  readonly results = signal<NearbySpace[]>([]);
  readonly center = signal<{ latitude: number; longitude: number } | null>(null);
  readonly hovered = signal<string | null>(null);
  readonly loading = signal(false);
  readonly searched = signal(false);
  readonly locating = signal(false);
  readonly mapOpen = signal(false);
  readonly error = signal<string | null>(null);

  readonly availableNow = signal(false);
  readonly priceIndex = signal(0);
  readonly vehicleIndex = signal(0);

  readonly placeLabel = signal('');
  readonly whenLabel = signal('');

  priceLabel(): string {
    return PRICE_OPTIONS[this.priceIndex()].label;
  }

  vehicleLabel(): string {
    return VEHICLE_OPTIONS[this.vehicleIndex()].label;
  }

  distance(space: NearbySpace): string {
    return describeDistance(space.distanceMetres);
  }

  ngOnInit(): void {
    this.refreshLabels();
    const state = this.search.state();
    if (state.latitude != null && state.longitude != null) {
      this.center.set({ latitude: state.latitude, longitude: state.longitude });
      this.runSearch();
    } else {
      void this.locate();
    }
  }

  /** The bar was edited: remember it, geocode it, search. */
  applySearch(): void {
    this.search.update({ city: this.city, area: this.area, startLocal: this.startLocal, hours: this.hours, latitude: null, longitude: null });
    this.refreshLabels();
    void this.locate();
  }

  toggleNow(): void {
    this.availableNow.set(!this.availableNow());
    if (this.availableNow()) {
      this.startLocal = toLocalInput(new Date(Date.now() + 5 * 60_000));
      this.search.update({ startLocal: this.startLocal });
      this.refreshLabels();
    }
  }

  cyclePrice(): void {
    this.priceIndex.set((this.priceIndex() + 1) % PRICE_OPTIONS.length);
    this.runSearch();
  }

  cycleVehicle(): void {
    this.vehicleIndex.set((this.vehicleIndex() + 1) % VEHICLE_OPTIONS.length);
    this.runSearch();
  }

  widen(): void {
    this.radius = Math.min(this.radius * 2, 25_000);
    this.runSearch();
  }

  toggleMap(): void {
    this.mapOpen.set(!this.mapOpen());
    this.mapView?.refresh();
  }

  open(space: NearbySpace): void {
    void this.router.navigate(['/spaces', space.id]);
  }

  async useMyLocation(): Promise<void> {
    this.locating.set(true);
    this.error.set(null);
    try {
      const here = await this.location.current();
      this.area = '';
      this.placeLabel.set('you');
      this.search.update({ area: '', latitude: here.latitude, longitude: here.longitude });
      this.center.set({ latitude: here.latitude, longitude: here.longitude });
      this.runSearch();
    } catch (err) {
      this.error.set((err as Error).message);
    } finally {
      this.locating.set(false);
    }
  }

  private async locate(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const hit = (await this.search.geocode(this.area, this.city)) ?? (await this.search.geocode('', this.city));
      if (!hit) {
        this.loading.set(false);
        this.error.set(`We couldn't place "${this.area || this.city}". Try a nearby area or use your location.`);
        return;
      }
      this.search.update({ latitude: hit.latitude, longitude: hit.longitude });
      this.center.set({ latitude: hit.latitude, longitude: hit.longitude });
      this.runSearch();
    } catch {
      this.loading.set(false);
      this.error.set('The map service did not answer. Check your connection and try again.');
    }
  }

  private runSearch(): void {
    const at = this.center();
    if (!at) {
      return;
    }
    this.loading.set(true);
    this.error.set(null);

    this.api
      .searchNearby(
        at.latitude,
        at.longitude,
        this.radius,
        PRICE_OPTIONS[this.priceIndex()].max ?? undefined,
        VEHICLE_OPTIONS[this.vehicleIndex()].type ?? undefined,
      )
      .subscribe({
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

  private refreshLabels(): void {
    this.placeLabel.set(this.area.trim() ? this.area.trim() : this.city);
    this.whenLabel.set(`${describeLocal(this.startLocal)} for ${this.hours}h`);
  }
}
