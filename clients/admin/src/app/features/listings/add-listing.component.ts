import { Component, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { environment } from '../../../environments/environment';
import { ApiService } from '../../core/api.service';
import { LocationService } from '../../shared/location.service';
import { MapPickerComponent } from '../../shared/map-picker.component';
import { SearchService } from '../../shared/search.service';
import { AvailabilityWindowRequest, City, DAY_NAMES, ListingPhoto, VehicleType } from '../../core/models';

interface DayRow {
  open: boolean;
  allDay: boolean;
  from: string;
  to: string;
}

@Component({
  selector: 'app-add-listing',
  standalone: true,
  imports: [FormsModule, RouterLink, MapPickerComponent],
  template: `
    <div class="stack">
      <a routerLink="/listings">← Back to listings</a>
      <h1>List a space</h1>

      @if (error(); as message) {
        <div class="banner banner--error" role="alert">{{ message }}</div>
      }

      @if (created(); as listing) {
        <!-- Step two, on the same page: a listing without a photo is the one renters scroll
             past, and sending the host off to find the upload on another screen is how a
             listing ends up without one. -->
        <section class="card stack">
          <div>
            <h2>{{ listing.status === 'Published' ? 'Live. Now add photos' : 'Saved. Now add photos' }}</h2>
            <p class="muted">Up to six. The first one is the cover renters see in search.</p>
          </div>

          @if (publishFailure(); as message) {
            <div class="banner banner--warn" role="status">{{ message }}</div>
          }

          <div class="photos">
            @for (p of photos(); track p.id) {
              <div class="photo shot"><img [src]="asset(p.url)" alt="" /></div>
            }
            @if (photos().length < 6) {
              <label class="photo add-photo">
                <input type="file" accept="image/*" multiple (change)="upload($event)" [disabled]="busy()" />
                <span>{{ busy() ? 'Uploading…' : '+ Add photo' }}</span>
              </label>
            }
          </div>

          <div class="row">
            <a class="btn primary" routerLink="/listings">Done</a>
            <a class="btn" [routerLink]="['/spaces', listing.id]">See it as a renter</a>
          </div>
        </section>
      } @else {
      <form class="stack" (ngSubmit)="save()">
        <section class="card stack">
          <h2>The space</h2>

          <div class="grid">
            <div class="span-2">
              <label for="title">Title</label>
              <input id="title" name="title" [(ngModel)]="form.title" placeholder="Covered driveway, quiet street" />
            </div>

            <div class="span-2">
              <label for="address">Address</label>
              <input id="address" name="address" [(ngModel)]="form.addressLine" />
            </div>

            <div>
              <label for="city">City</label>
              <!-- A list, not a box: the price band is looked up by this name, and a renter who
                   searched Bengaluru must never be shown a driveway whose host typed Gurgaon. -->
              <select id="city" name="city" [(ngModel)]="form.city" (ngModelChange)="onCityChange()">
                @for (c of cities(); track c.name) {
                  <option [value]="c.name">{{ c.name }}, {{ c.state }}</option>
                }
              </select>
              @if (selectedCity(); as c) {
                @if (!c.hasPricing) {
                  <small class="muted">
                    No price band for {{ c.name }} yet. You can save a draft; publishing waits for one.
                  </small>
                }
              }
              <button type="button" class="ghost sm ask-toggle" (click)="askingForCity.set(!askingForCity())">
                Don't see your city? Ask for it
              </button>
              @if (askingForCity()) {
                <div class="ask stack">
                  <input name="askCity" [(ngModel)]="askCity" placeholder="Which city?" />
                  <input name="askNote" [(ngModel)]="askNote" placeholder="Anything we should know (optional)" />
                  <div class="row">
                    <button type="button" class="sm" [disabled]="busy() || !askCity.trim()" (click)="requestCity()">
                      Send the request
                    </button>
                    @if (asked()) {
                      <span class="muted small">Noted — thanks. We open cities by demand.</span>
                    }
                  </div>
                </div>
              }
            </div>

            <div>
              <label for="zone">Zone (optional)</label>
              <input id="zone" name="zone" [(ngModel)]="zone" placeholder="cbd" />
            </div>
          </div>

          <div>
            <label>Where exactly is it?</label>
            <div class="row">
              <button type="button" class="sm" [disabled]="locating()" (click)="useMyLocation()">
                {{ locating() ? 'Locating…' : 'Use my location' }}
              </button>
              <button type="button" class="sm" [disabled]="locating() || !form.addressLine.trim()" (click)="findAddress()">
                {{ locating() ? 'Finding…' : 'Find the address on the map' }}
              </button>
              <span class="muted small">Then drag the pin onto the entrance.</span>
            </div>
          </div>

          <!-- The pin drives the PostGIS search renters use to find this space, so getting it
               right matters more than any other field on the form. -->
          <app-map-picker
            #picker
            [latitude]="form.latitude"
            [longitude]="form.longitude"
            [zoom]="16"
            [height]="300"
            (pointChanged)="onPointChanged($event)"
          />
        </section>

        <section class="card stack">
          <h2>Price and vehicles</h2>

          <div class="grid">
            <div>
              <label for="price">Price per hour</label>
              <input id="price" name="price" type="number" min="0" step="5" [(ngModel)]="form.pricePerHour" />
              <small class="muted">Must sit inside your city's band, or publishing is refused.</small>
            </div>

            <div>
              <label>Vehicles accepted</label>
              <div class="checks">
                <label class="inline">
                  <input type="checkbox" name="car" [(ngModel)]="acceptsCar" /> Car
                </label>
                <label class="inline">
                  <input type="checkbox" name="bike" [(ngModel)]="acceptsBike" /> Two-wheeler
                </label>
              </div>
            </div>

            <div>
              <label for="tz">Time zone</label>
              <input id="tz" name="tz" [(ngModel)]="form.timeZoneId" />
              <small class="muted">Set from the city. Your opening hours are read in this zone.</small>
            </div>
          </div>
        </section>

        <section class="card stack">
          <div>
            <h2>When it's available</h2>
            <p class="muted">
              A space with no hours cannot be published. Tick "all day" for 24 hours, or set a
              window — an end earlier than the start runs overnight.
            </p>
          </div>

          <div class="days">
            @for (day of days; track $index) {
              <div class="day" [class.day--off]="!day.open">
                <label class="inline">
                  <input type="checkbox" [(ngModel)]="day.open" [name]="'open' + $index" />
                  <strong>{{ dayName($index) }}</strong>
                </label>

                @if (day.open) {
                  <label class="inline">
                    <input type="checkbox" [(ngModel)]="day.allDay" [name]="'allday' + $index" />
                    all day
                  </label>

                  @if (!day.allDay) {
                    <div class="times">
                      <input type="time" [(ngModel)]="day.from" [name]="'from' + $index" />
                      <span class="muted">to</span>
                      <input type="time" [(ngModel)]="day.to" [name]="'to' + $index" />
                    </div>
                  }
                }
              </div>
            }
          </div>

          <div class="row">
            <button type="button" (click)="applyToAll()">Copy Monday to every day</button>
          </div>
        </section>

        <div class="row">
          <button class="primary" type="submit" [disabled]="busy()">
            {{ publishNow ? 'Create and publish' : 'Save as draft' }}
          </button>
          <label class="inline">
            <input type="checkbox" name="publishNow" [(ngModel)]="publishNow" /> publish immediately
          </label>
        </div>
      </form>
      }
    </div>
  `,
  styles: [
    `
      .grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
        gap: var(--space-4);
      }

      .span-2 {
        grid-column: span 2;
      }

      @media (max-width: 620px) {
        .span-2 {
          grid-column: span 1;
        }
      }

      small {
        display: block;
        margin-top: var(--space-1);
        font-size: 0.78rem;
      }

      label.inline {
        display: inline-flex;
        align-items: center;
        gap: var(--space-2);
        margin: 0;
        font-weight: 400;
      }

      label.inline input {
        width: auto;
      }

      .checks {
        display: flex;
        gap: var(--space-4);
        padding-top: var(--space-2);
      }

      .days {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(230px, 1fr));
        gap: var(--space-3);
      }

      .day {
        display: flex;
        flex-direction: column;
        gap: var(--space-2);
        padding: var(--space-3);
        border: 1px solid var(--border);
        border-radius: var(--radius);
      }

      .day--off {
        opacity: 0.6;
      }

      .times {
        display: flex;
        align-items: center;
        gap: var(--space-2);
      }

      .ask-toggle {
        margin-top: 6px;
        padding: 0;
        height: auto;
        font-weight: 600;
      }

      .ask {
        margin-top: 8px;
        padding: 12px;
        border: 1px dashed var(--border-strong);
        border-radius: var(--r-input);
      }

      .photos {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
        gap: 12px;
      }

      .shot {
        aspect-ratio: 4 / 3;
        overflow: hidden;
      }

      .shot img {
        width: 100%;
        height: 100%;
        object-fit: cover;
      }

      .add-photo {
        aspect-ratio: 4 / 3;
        display: flex;
        align-items: center;
        justify-content: center;
        border: 1px dashed var(--border-strong);
        cursor: pointer;
        font-weight: 600;
        color: var(--accent-600);
      }

      .add-photo input {
        display: none;
      }
    `,
  ],
})
export class AddListingComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly location = inject(LocationService);
  private readonly search = inject(SearchService);

  @ViewChild('picker') picker?: MapPickerComponent;

  readonly cities = signal<City[]>([]);
  readonly askingForCity = signal(false);
  readonly asked = signal(false);
  askCity = '';
  askNote = '';

  /** Set once the draft exists; the page then shows the photo step instead of the form. */
  readonly created = signal<{ id: string; status: string } | null>(null);
  readonly photos = signal<ListingPhoto[]>([]);
  readonly publishFailure = signal<string | null>(null);

  readonly selectedCity = computed(() => this.cities().find((c) => c.name === this.form.city) ?? null);

  ngOnInit(): void {
    this.api.cities().subscribe({
      next: (cities) => {
        this.cities.set(cities);
        if (!cities.some((c) => c.name === this.form.city) && cities[0]) {
          this.form.city = cities[0].name;
        }
        this.onCityChange();
      },
      error: (err: Error) => this.error.set(err.message),
    });
  }

  /** The pin and the clock follow the city, so a host in Pune does not start on Bengaluru. */
  onCityChange(): void {
    const city = this.selectedCity();
    if (!city) {
      return;
    }
    this.form.timeZoneId = city.timeZoneId;
    // Only recentre the pin if it has not been placed: a host who already dragged it to their
    // gate should not lose that to a city they picked afterwards.
    if (!this.pinPlaced) {
      this.form.latitude = city.latitude;
      this.form.longitude = city.longitude;
      this.picker?.moveTo(city.latitude, city.longitude);
    }
  }

  requestCity(): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.requestCity(this.askCity.trim(), this.askNote.trim() || null).subscribe({
      next: () => {
        this.busy.set(false);
        this.asked.set(true);
      },
      error: (err: Error) => {
        this.busy.set(false);
        this.error.set(err.message);
      },
    });
  }

  asset(url: string): string {
    return url.startsWith('http') ? url : `${environment.apiBaseUrl}${url}`;
  }

  /** Several files, one after another: the API takes one per request. */
  upload(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    const listing = this.created();
    input.value = '';

    if (!listing || files.length === 0) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    const next = (index: number): void => {
      if (index >= files.length || this.photos().length >= 6) {
        this.busy.set(false);
        return;
      }

      this.api.addListingPhoto(listing.id, files[index]).subscribe({
        next: (photo) => {
          this.photos.update((current) => [...current, photo]);
          next(index + 1);
        },
        error: (err: Error) => {
          this.busy.set(false);
          this.error.set(err.message);
        },
      });
    };

    next(0);
  }

  private pinPlaced = false;

  form = {
    title: '',
    addressLine: '',
    city: 'Bengaluru',
    latitude: 12.9716,
    longitude: 77.5946,
    pricePerHour: 60,
    timeZoneId: 'Asia/Kolkata',
  };

  zone = '';
  acceptsCar = true;
  acceptsBike = false;
  publishNow = true;

  days: DayRow[] = DAY_NAMES.map(() => ({ open: true, allDay: true, from: '09:00', to: '18:00' }));

  readonly busy = signal(false);
  readonly locating = signal(false);
  readonly error = signal<string | null>(null);

  async useMyLocation(): Promise<void> {
    this.locating.set(true);
    this.error.set(null);

    try {
      const position = await this.location.current();
      this.pinPlaced = true;
      this.form.latitude = round6(position.latitude);
      this.form.longitude = round6(position.longitude);
      this.picker?.moveTo(this.form.latitude, this.form.longitude);
    } catch (err) {
      this.error.set((err as Error).message);
    } finally {
      this.locating.set(false);
    }
  }

  /** Geocodes the typed address so the host never has to think about coordinates. */
  async findAddress(): Promise<void> {
    this.locating.set(true);
    this.error.set(null);
    try {
      const hit = await this.search.geocode(this.form.addressLine, this.form.city);
      if (!hit) {
        this.error.set("We couldn't find that address. Drag the pin onto the entrance instead.");
        return;
      }
      this.pinPlaced = true;
      this.form.latitude = hit.latitude;
      this.form.longitude = hit.longitude;
      this.picker?.moveTo(hit.latitude, hit.longitude);
    } catch {
      this.error.set('The map service did not answer. Drag the pin onto the entrance instead.');
    } finally {
      this.locating.set(false);
    }
  }

  onPointChanged(point: { latitude: number; longitude: number }): void {
    this.pinPlaced = true;
    this.form.latitude = round6(point.latitude);
    this.form.longitude = round6(point.longitude);
  }

  dayName(index: number): string {
    return DAY_NAMES[index];
  }

  /** Most hosts keep the same hours all week; typing them seven times is busywork. */
  applyToAll(): void {
    const monday = this.days[1];
    this.days = this.days.map(() => ({ ...monday }));
  }

  save(): void {
    const vehicleTypes: VehicleType[] = [];
    if (this.acceptsCar) {
      vehicleTypes.push('FourWheeler');
    }
    if (this.acceptsBike) {
      vehicleTypes.push('TwoWheeler');
    }

    if (vehicleTypes.length === 0) {
      this.error.set('Pick at least one vehicle type.');
      return;
    }

    const windows: AvailabilityWindowRequest[] = [];
    this.days.forEach((day, index) => {
      if (!day.open) {
        return;
      }

      // Equal start and end is how the API expresses a full 24 hours.
      windows.push(
        day.allDay
          ? { dayOfWeek: index, startTime: '00:00:00', endTime: '00:00:00' }
          : { dayOfWeek: index, startTime: day.from + ':00', endTime: day.to + ':00' },
      );
    });

    if (windows.length === 0) {
      this.error.set('Open the space on at least one day.');
      return;
    }

    if (!this.form.title.trim() || !this.form.addressLine.trim() || !this.form.city.trim()) {
      this.error.set('Title, address and city are required.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    this.api
      .createListing({
        ...this.form,
        zone: this.zone.trim() || null,
        supportedVehicleTypes: vehicleTypes,
        availabilityWindows: windows,
      })
      .subscribe({
        next: (created) => {
          if (!this.publishNow) {
            this.busy.set(false);
            this.created.set({ id: created.id, status: 'Draft' });
            return;
          }

          this.api.publishListing(created.id).subscribe({
            next: () => {
              this.busy.set(false);
              this.created.set({ id: created.id, status: 'Published' });
            },
            error: (err: Error) => {
              this.busy.set(false);
              // The draft was saved; only publishing failed (usually the price band). Still
              // worth adding photos to.
              this.publishFailure.set(err.message + ' The listing was saved as a draft.');
              this.created.set({ id: created.id, status: 'Draft' });
            },
          });
        },
        error: (err: Error) => {
          this.busy.set(false);
          this.error.set(err.message);
        },
      });
  }
}

/** Six decimals is roughly 0.1 m - more precision than a phone GPS can justify. */
function round6(value: number): number {
  return Math.round(value * 1e6) / 1e6;
}
