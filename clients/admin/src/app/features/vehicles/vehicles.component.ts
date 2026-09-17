import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { ApiService } from '../../core/api.service';
import { Vehicle, VehicleType } from '../../core/models';

@Component({
  selector: 'app-vehicles',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="stack">
      <div>
        <h1>My vehicles</h1>
        <p class="muted">
          A booking has to name one of your vehicles. The plate is also what number-plate
          recognition will match against once automatic detection is switched on.
        </p>
      </div>

      @if (error(); as message) {
        <div class="banner banner--error" role="alert">{{ message }}</div>
      }

      <section class="card">
        <form class="row" (ngSubmit)="add()">
          <div class="grow">
            <label for="plate">Number plate</label>
            <input
              id="plate"
              name="plate"
              [(ngModel)]="plate"
              placeholder="KA 01 AB 1234"
              autocomplete="off"
            />
          </div>

          <div>
            <label for="type">Type</label>
            <select id="type" name="type" [(ngModel)]="type">
              <option value="FourWheeler">Car</option>
              <option value="TwoWheeler">Two-wheeler</option>
            </select>
          </div>

          <button class="primary" type="submit" [disabled]="busy()">Add vehicle</button>
        </form>
        <!-- Spacing and case are normalised server-side, so "ka 01-ab 1234" is the same car. -->
        <small class="muted">Spaces and dashes are ignored.</small>
      </section>

      <section class="card stack">
        <h2>Registered</h2>

        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (vehicles().length === 0) {
          <p class="muted">No vehicles yet. Add one above before booking a space.</p>
        } @else {
          <div class="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>Plate</th>
                  <th>Type</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (vehicle of vehicles(); track vehicle.id) {
                  <tr>
                    <td><strong>{{ vehicle.plateNumber }}</strong></td>
                    <td>{{ vehicle.type === 'FourWheeler' ? 'Car' : 'Two-wheeler' }}</td>
                    <td>
                      <button type="button" [disabled]="busy()" (click)="remove(vehicle.id)">
                        Remove
                      </button>
                    </td>
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
      form.row {
        align-items: flex-end;
      }

      .grow {
        flex: 1 1 220px;
      }

      small {
        display: block;
        margin-top: var(--space-2);
        font-size: 0.8rem;
      }
    `,
  ],
})
export class VehiclesComponent implements OnInit {
  private readonly api = inject(ApiService);

  plate = '';
  type: VehicleType = 'FourWheeler';

  readonly vehicles = signal<Vehicle[]>([]);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.load();
  }

  add(): void {
    if (!this.plate.trim()) {
      this.error.set('Enter a number plate.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    this.api.addVehicle(this.plate, this.type).subscribe({
      next: () => {
        this.busy.set(false);
        this.plate = '';
        this.load();
      },
      error: (err: Error) => {
        this.busy.set(false);
        this.error.set(err.message);
      },
    });
  }

  remove(vehicleId: string): void {
    this.busy.set(true);
    this.error.set(null);

    this.api.removeVehicle(vehicleId).subscribe({
      next: () => {
        this.busy.set(false);
        this.load();
      },
      error: (err: Error) => {
        this.busy.set(false);
        // The API refuses while a booking is still open on the vehicle, which is worth showing.
        this.error.set(err.message);
      },
    });
  }

  private load(): void {
    this.loading.set(true);

    this.api.myVehicles().subscribe({
      next: (vehicles) => {
        this.vehicles.set(vehicles);
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }
}
