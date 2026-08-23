import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { ApiService } from '../../core/api.service';
import {
  CityPricingConfig,
  PricingBandChange,
  UpsertBandRequest,
  VehicleType,
} from '../../core/models';

@Component({
  selector: 'app-pricing',
  standalone: true,
  imports: [CurrencyPipe, DatePipe, FormsModule],
  template: `
    <div class="stack">
      <div>
        <h1>Pricing bands</h1>
        <p class="muted">
          Min and max hourly rate a host may charge, per city, zone and vehicle type. Bands are
          data, not code — changes take effect without a deploy, which is also why every one of
          them is recorded below.
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

          <div class="wide">
            <label for="reason">Reason</label>
            <input
              id="reason"
              name="reason"
              [ngModel]="form.reason ?? ''"
              (ngModelChange)="form.reason = $event || null"
              placeholder="Festival demand in the CBD"
            />
            <!-- Optional, because a required field produces "update" and nothing else. Asked for
                 anyway: the numbers are already in the history, the reason is the only part of a
                 price change that cannot be reconstructed later. -->
            <small class="muted">Optional, and the one thing the history cannot infer.</small>
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
                  <tr [class.inactive]="!band.isActive">
                    <td>{{ band.city }}</td>
                    <td>{{ band.zone ?? '—' }}</td>
                    <td>{{ band.vehicleType }}</td>
                    <td class="numeric">{{ band.minPricePerHour | currency: 'INR' : 'symbol' : '1.2-2' }}</td>
                    <td class="numeric">{{ band.maxPricePerHour | currency: 'INR' : 'symbol' : '1.2-2' }}</td>
                    <td class="numeric">{{ band.overstayMultiplier }}</td>
                    <td>{{ band.isActive ? 'Yes' : 'No' }}</td>
                    <td class="row-actions">
                      <button type="button" (click)="edit(band)">Edit</button>
                      <button type="button" (click)="toggleActive(band)" [disabled]="busy()">
                        {{ band.isActive ? 'Deactivate' : 'Activate' }}
                      </button>
                      <button type="button" (click)="showHistory(band)">History</button>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>

      <section class="card stack">
        <div class="history-head">
          <h2>
            @if (scopedBand(); as band) {
              Changes to {{ band.city }}{{ band.zone ? ' / ' + band.zone : '' }} ({{ band.vehicleType }})
            } @else {
              Recent changes
            }
          </h2>

          @if (scopedBand()) {
            <button type="button" (click)="showHistory(null)">Show all</button>
          }
        </div>

        @if (historyLoading()) {
          <p class="muted">Loading…</p>
        } @else if (history().length === 0) {
          <p class="muted">Nothing recorded yet.</p>
        } @else {
          <div class="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>When</th>
                  <th>Band</th>
                  <th>Change</th>
                  <th>Was</th>
                  <th>Became</th>
                  <th>Reason</th>
                </tr>
              </thead>
              <tbody>
                @for (change of history(); track change.id) {
                  <tr>
                    <td>{{ change.changedAt | date: 'medium' }}</td>
                    <td>
                      {{ change.city }}{{ change.zone ? ' / ' + change.zone : '' }}
                      <span class="muted">· {{ change.vehicleType }}</span>
                    </td>
                    <td><span class="tag tag--{{ change.kind.toLowerCase() }}">{{ change.kind }}</span></td>
                    <!-- A Created row has no "was", and printing 0.00 there would read as a band
                         that used to be free rather than one that did not exist. -->
                    <td>{{ change.previousMinPricePerHour === null ? '—' : describePrevious(change) }}</td>
                    <td>{{ describeCurrent(change) }}</td>
                    <td>{{ change.reason ?? '—' }}</td>
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

      .wide {
        grid-column: 1 / -1;
      }

      .actions {
        display: flex;
        gap: var(--space-2);
      }

      .row-actions {
        display: flex;
        gap: var(--space-2);
        white-space: nowrap;
      }

      .history-head {
        display: flex;
        align-items: baseline;
        justify-content: space-between;
        gap: var(--space-4);
      }

      /* A deactivated band still shows its numbers — dimmed, not hidden, because "what did this
         city allow before we switched it off" is exactly the question this screen gets asked. */
      tr.inactive td {
        opacity: 0.55;
      }

      .tag {
        display: inline-block;
        padding: 0.1rem 0.5rem;
        border-radius: 999px;
        font-size: 0.75rem;
        border: 1px solid currentColor;
      }

      .tag--deactivated {
        color: var(--danger, #b3261e);
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
  readonly history = signal<PricingBandChange[]>([]);
  readonly scopedBand = signal<CityPricingConfig | null>(null);
  readonly loading = signal(true);
  readonly historyLoading = signal(true);
  readonly busy = signal(false);
  readonly saved = signal(false);
  readonly error = signal<string | null>(null);
  readonly isExisting = signal(false);

  form: UpsertBandRequest = this.blank();

  ngOnInit(): void {
    this.load();
    this.loadHistory(null);
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
      // Deliberately not carried over from the previous edit. A reason belongs to one change, and
      // a stale one silently attached to the next is worse than no reason at all.
      reason: null,
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
        this.loadHistory(this.scopedBand()?.id ?? null);
      },
      error: (err: Error) => {
        this.busy.set(false);
        this.error.set(err.message);
      },
    });
  }

  toggleActive(band: CityPricingConfig): void {
    const turningOff = band.isActive;

    // Confirmed only in the disruptive direction. Switching a band off stops every listing in that
    // city or zone from publishing or repricing, and it is one click away from Edit.
    if (turningOff && !confirm(`Deactivate the ${band.city} band? No listing there can be published or repriced while it is off.`)) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    this.api.setBandActive(band.id, { isActive: !band.isActive, reason: null }).subscribe({
      next: () => {
        this.busy.set(false);
        this.load();
        this.loadHistory(this.scopedBand()?.id ?? null);
      },
      error: (err: Error) => {
        this.busy.set(false);
        this.error.set(err.message);
      },
    });
  }

  showHistory(band: CityPricingConfig | null): void {
    this.scopedBand.set(band);
    this.loadHistory(band?.id ?? null);
  }

  describePrevious(change: PricingBandChange): string {
    return this.describe(
      change.previousMinPricePerHour,
      change.previousMaxPricePerHour,
      change.previousOverstayMultiplier,
      change.previousIsActive,
    );
  }

  describeCurrent(change: PricingBandChange): string {
    return this.describe(
      change.minPricePerHour,
      change.maxPricePerHour,
      change.overstayMultiplier,
      change.isActive,
    );
  }

  private describe(
    min: number | null,
    max: number | null,
    multiplier: number | null,
    active: boolean | null,
  ): string {
    if (min === null || max === null) {
      return '—';
    }

    const range = `₹${min.toFixed(2)}–${max.toFixed(2)}/hr`;
    const overstay = multiplier === null ? '' : ` · ${multiplier}×`;
    const state = active === false ? ' · off' : '';
    return `${range}${overstay}${state}`;
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

  private loadHistory(bandId: string | null): void {
    this.historyLoading.set(true);

    this.api.bandHistory(bandId ?? undefined).subscribe({
      next: (changes) => {
        this.history.set(changes);
        this.historyLoading.set(false);
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.historyLoading.set(false);
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
      reason: null,
    };
  }
}
