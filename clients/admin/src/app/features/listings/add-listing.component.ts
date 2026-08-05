import { Component, ViewChild, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { ApiService } from '../../core/api.service';
import { LocationService } from '../../shared/location.service';
import { MapPickerComponent } from '../../shared/map-picker.component';
import { AvailabilityWindowRequest, DAY_NAMES, VehicleType } from '../../core/models';

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
              <input id="city" name="city" [(ngModel)]="form.city" />
            </div>

            <div>
              <label for="zone">Zone (optional)</label>
              <input id="zone" name="zone" [(ngModel)]="zone" placeholder="cbd" />
            </div>

            <div>
              <label for="lat">Latitude</label>
              <input id="lat" name="lat" type="number" step="0.0001" [(ngModel)]="form.latitude" />
            </div>

            <div>
              <label for="lng">Longitude</label>
              <input id="lng" name="lng" type="number" step="0.0001" [(ngModel)]="form.longitude" />
            </div>
          </div>

          <div class="row">
            <button type="button" [disabled]="locating()" (click)="useMyLocation()">
              {{ locating() ? 'Locating…' : 'Use my location' }}
            </button>
            <span class="muted small">Or click the map / drag the pin.</span>
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
              <small class="muted">Your opening hours are read in this zone.</small>
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
    `,
  ],
})
export class AddListingComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly location = inject(LocationService);

  @ViewChild('picker') picker?: MapPickerComponent;

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
      this.form.latitude = round6(position.latitude);
      this.form.longitude = round6(position.longitude);
      this.picker?.moveTo(this.form.latitude, this.form.longitude);
    } catch (err) {
      this.error.set((err as Error).message);
    } finally {
      this.locating.set(false);
    }
  }

  onPointChanged(point: { latitude: number; longitude: number }): void {
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
            void this.router.navigate(['/listings']);
            return;
          }

          this.api.publishListing(created.id).subscribe({
            next: () => {
              this.busy.set(false);
              void this.router.navigate(['/listings']);
            },
            error: (err: Error) => {
              this.busy.set(false);
              // The draft was saved; only publishing failed (usually the price band).
              this.error.set(err.message + ' The listing was saved as a draft.');
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
