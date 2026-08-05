import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { ApiService } from '../../core/api.service';
import { BookingStatus, BookingSummary } from '../../core/models';
import { StatusPillComponent } from '../../shared/status-pill.component';

type Scope = 'renting' | 'hosting';

@Component({
  selector: 'app-bookings',
  standalone: true,
  imports: [CurrencyPipe, DatePipe, FormsModule, RouterLink, StatusPillComponent],
  template: `
    <div class="stack">
      <h1>Bookings</h1>

      <div class="row">
        <div class="toggle">
          <button
            type="button"
            [class.primary]="scope() === 'renting'"
            (click)="setScope('renting')"
          >
            As renter
          </button>
          <button
            type="button"
            [class.primary]="scope() === 'hosting'"
            (click)="setScope('hosting')"
          >
            As host
          </button>
        </div>

        <div class="filter">
          <select [ngModel]="status()" (ngModelChange)="setStatus($event)" aria-label="Filter by status">
            <option [ngValue]="undefined">All statuses</option>
            @for (option of statuses; track option) {
              <option [ngValue]="option">{{ option }}</option>
            }
          </select>
        </div>
      </div>

      @if (error(); as message) {
        <div class="banner banner--error" role="alert">{{ message }}</div>
      }

      @if (loading()) {
        <p class="muted">Loading…</p>
      } @else if (bookings().length === 0) {
        <p class="muted">
          {{ scope() === 'renting' ? 'You have not booked any spaces.' : 'Nobody has booked your spaces yet.' }}
        </p>
      } @else {
        <div class="card table-scroll">
          <table>
            <thead>
              <tr>
                <th>Space</th>
                <th>Starts</th>
                <th>Ends</th>
                <th class="numeric">Rate</th>
                <th class="numeric">Held</th>
                <th class="numeric">Settled</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              @for (booking of bookings(); track booking.id) {
                <tr>
                  <td>
                    <a [routerLink]="['/bookings', booking.id]">{{ booking.spaceTitle }}</a>
                    <div class="muted small">{{ booking.spaceAddress }}</div>
                  </td>
                  <td>{{ booking.startTime | date: 'short' }}</td>
                  <td>
                    {{ (booking.actualEndTime ?? booking.expectedEndTime) | date: 'short' }}
                    @if (!booking.actualEndTime) {
                      <span class="muted small"> (expected)</span>
                    }
                  </td>
                  <td class="numeric">{{ booking.ratePerHour | currency: 'INR' : 'symbol' : '1.2-2' }}</td>
                  <td class="numeric">{{ booking.holdAmount | currency: 'INR' : 'symbol' : '1.2-2' }}</td>
                  <td class="numeric">{{ booking.settledAmount | currency: 'INR' : 'symbol' : '1.2-2' }}</td>
                  <td><app-status-pill [status]="booking.status" /></td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
  styles: [
    `
      .toggle {
        display: flex;
        gap: var(--space-1);
      }

      .filter {
        min-width: 180px;
      }

      .small {
        font-size: 0.8rem;
      }
    `,
  ],
})
export class BookingsComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly statuses: BookingStatus[] = [
    'Held',
    'Active',
    'Completed',
    'InViolation',
    'Disputed',
    'Cancelled',
  ];

  readonly scope = signal<Scope>('renting');
  readonly status = signal<BookingStatus | undefined>(undefined);
  readonly bookings = signal<BookingSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.load();
  }

  setScope(scope: Scope): void {
    if (scope === this.scope()) {
      return;
    }

    this.scope.set(scope);
    this.load();
  }

  setStatus(status: BookingStatus | undefined): void {
    this.status.set(status);
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);

    const request =
      this.scope() === 'renting'
        ? this.api.myBookings(this.status())
        : this.api.hostingBookings(this.status());

    request.subscribe({
      next: (bookings) => {
        this.bookings.set(bookings);
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }
}
