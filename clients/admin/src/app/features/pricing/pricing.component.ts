import { CurrencyPipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { ApiService } from '../../core/api.service';
import { CityPricingConfig, UpsertBandRequest, VehicleType } from '../../core/models';

@Component({
  selector: 'app-pricing',
  standalone: true,
  imports: [CurrencyPipe, FormsModule],
  template: `
    <div class="stack">
      <div>
        <h1>Pricing bands</h1>
        <p class="muted">
          Min and max hourly rate a host may charge, per city, zone and vehicle type. Bands are
          data, not code — changes take effect without a deploy.
        </p>
      </div>

      @if (error(); as message) {
        <div class="banner banner--error" role="alert">{{ message }}</div>
      }

      @if (saved()) {
        <div class="banner banner--info" role="status">Band saved.</div>
      }

      <section class="card stack">
        <h2>{{ isExisting() ? 'Update band' : 'Add band' }}</h2>

        <form class="grid" (ngSubmit)="save()">
          <div>
            <label for="city">City</label>
            <input id="city" name="city" [(ngModel)]="form.city" required />
          </div>

          <div>
            <label for="zone">Zone (optional)</label>
            <input
              id="zone"
              name="zone"
              [ngModel]="form.zone ?? ''"
              (ngModelChange)="form.zone = $event || null"
              placeholder="cbd"
            />
          </div>

          <div>
            <label for="vehicleType">Vehicle type</label>
            <select id="vehicleType" name="vehicleType" [(ngModel)]="form.vehicleType">
              <option value="TwoWheeler">Two-wheeler</option>
              <option value="FourWheeler">Four-wheeler</option>
            </select>
          </div>

          <div>
            <label for="min">Min / hour</label>
            <input id="min" name="min" type="number" min="0" step="1" [(ngModel)]="form.minPricePerHour" />
          </div>

          <div>
            <label for="max">Max / hour</label>
            <input id="max" name="max" type="number" min="0" step="1" [(ngModel)]="form.maxPricePerHour" />
          </div>

          <div>
            <label for="multiplier">Overstay multiplier</label>
            <input
              id="multiplier"
              name="multiplier"
              type="number"
              min="1"
              max="2"
              step="0.05"
              [(ngModel)]="form.overstayMultiplier"
            />
            <!-- The API rejects anything above the platform ceiling, so hosts cannot punitively
                 overcharge a renter whose car is stuck. -->
            <small class="muted">Capped at the platform ceiling (2.0).</small>
          </div>

          <div class="actions">
            <button class="primary" type="submit" [disabled]="busy()">Save band</button>
            @if (isExisting()) {
              <button type="button" (click)="reset()" [disabled]="busy()">Clear</button>
            }
          </div>
        </form>
      </section>

      <section class="card stack">
        <h2>Configured bands</h2>

        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (bands().length === 0) {
          <p class="muted">
            No bands configured. Until a city has one, no listing there can be published.
          </p>
        } @else {
          <div class="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>City</th>
                  <th>Zone</th>
                  <th>Vehicle</th>
                  <th class="numeric">Min</th>
                  <th class="numeric">Max</th>
                  <th class="numeric">Overstay ×</th>
                  <th>Active</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (band of bands(); track band.id) {
                  <tr>
                    <td>{{ band.city }}</td>
                    <td>{{ band.zone ?? '—' }}</td>
                    <td>{{ band.vehicleType }}</td>
                    <td class="numeric">{{ band.minPricePerHour | currency: 'INR' : 'symbol' : '1.2-2' }}</td>
                    <td class="numeric">{{ band.maxPricePerHour | currency: 'INR' : 'symbol' : '1.2-2' }}</td>
                    <td class="numeric">{{ band.overstayMultiplier }}</td>
                    <td>{{ band.isActive ? 'Yes' : 'No' }}</td>
                    <td><button type="button" (click)="edit(band)">Edit</button></td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>
    </div>
  `,
  styles: [
    `
      .grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
        gap: var(--space-4);
        align-items: end;
      }

      .actions {
        display: flex;
        gap: var(--space-2);
      }

      small {
        display: block;
        margin-top: var(--space-1);
        font-size: 0.78rem;
      }
    `,
  ],
})
export class PricingComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly bands = signal<CityPricingConfig[]>([]);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly saved = signal(false);
  readonly error = signal<string | null>(null);
  readonly isExisting = signal(false);

  form: UpsertBandRequest = this.blank();

  ngOnInit(): void {
    this.load();
  }

  edit(band: CityPricingConfig): void {
    this.form = {
      city: band.city,
      zone: band.zone,
      vehicleType: band.vehicleType,
      minPricePerHour: band.minPricePerHour,
      maxPricePerHour: band.maxPricePerHour,
      overstayMultiplier: band.overstayMultiplier,
      isActive: band.isActive,
    };

    this.isExisting.set(true);
    this.saved.set(false);
  }

  reset(): void {
    this.form = this.blank();
    this.isExisting.set(false);
  }

  save(): void {
    if (!this.form.city.trim()) {
      this.error.set('City is required.');
      return;
    }

    if (this.form.minPricePerHour > this.form.maxPricePerHour) {
      this.error.set('Minimum cannot exceed maximum.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.saved.set(false);

    this.api.upsertBand(this.form).subscribe({
      next: () => {
        this.busy.set(false);
        this.saved.set(true);
        this.reset();
        this.load();
      },
      error: (err: Error) => {
        this.busy.set(false);
        this.error.set(err.message);
      },
    });
  }

  private load(): void {
    this.loading.set(true);

    this.api.pricingBands().subscribe({
      next: (bands) => {
        this.bands.set(bands);
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }

  private blank(): UpsertBandRequest {
    return {
      city: '',
      zone: null,
      vehicleType: 'FourWheeler' as VehicleType,
      minPricePerHour: 20,
      maxPricePerHour: 120,
      overstayMultiplier: 1.25,
      isActive: true,
    };
  }
}
