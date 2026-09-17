import { DecimalPipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import { ApiService } from '../../core/api.service';
import { BookingSummary } from '../../core/models';
import { describeLocal } from '../../shared/search.service';

type Scope = 'renting' | 'hosting';
type Tab = 'upcoming' | 'active' | 'past';

/**
 * Bookings as three tabs — upcoming, active, past — rather than a status filter. A renter asks
 * "when am I parked next?", not "show me everything in Held".
 */
@Component({
  selector: 'app-bookings',
  standalone: true,
  imports: [DecimalPipe, RouterLink],
  template: `
    <div class="page-head">
      <h1>Bookings</h1>
      <div class="scope">
        <button type="button" class="chip" [class.selected]="scope() === 'renting'" (click)="setScope('renting')">My parking</button>
        <button type="button" class="chip" [class.selected]="scope() === 'hosting'" (click)="setScope('hosting')">At my spaces</button>
      </div>
    </div>

    <div class="tabs" role="tablist">
      @for (t of tabs; track t.key) {
        <button type="button" role="tab" [attr.aria-selected]="tab() === t.key" [class.on]="tab() === t.key" (click)="tab.set(t.key)">
          {{ t.label }} <span class="count">{{ count(t.key) }}</span>
        </button>
      }
    </div>

    @if (error(); as message) {
      <div class="banner banner--error" role="alert">{{ message }}</div>
    }

    @if (loading()) {
      <p class="muted">Loading…</p>
    } @else if (visible().length === 0) {
      <div class="empty">
        <div class="empty__mark"></div>
        @if (scope() === 'renting') {
          <h3>{{ tab() === 'past' ? 'No past bookings' : 'No bookings yet' }}</h3>
          <p>Find a space near you and it will show up here.</p>
          <a routerLink="/explore" class="btn primary">Find parking</a>
        } @else {
          <h3>Nobody has booked your spaces {{ tab() === 'past' ? 'before' : 'yet' }}</h3>
          <p>Published spaces appear in search as soon as they're live.</p>
          <a routerLink="/listings" class="btn primary">Your spaces</a>
        }
      </div>
    } @else {
      <div class="list">
        @for (b of visible(); track b.id) {
          <a class="item" [routerLink]="['/bookings', b.id]">
            <div class="photo thumb"><span class="photo__hint">space</span></div>
            <div class="body">
              <div class="top">
                <h3>{{ b.spaceTitle }}</h3>
                <span class="badge" [class]="badgeClass(b)">{{ statusLabel(b) }}</span>
              </div>
              <div class="meta">{{ when(b) }}</div>
              <div class="meta muted">{{ b.spaceAddress }}</div>
              @if (b.slotBlocked) {
                <div class="blocked">The space was still occupied when your slot began — cancelling is free.</div>
              }
            </div>
            <div class="money">
              <div class="amt">₹{{ (b.status === 'Completed' || b.status === 'InViolation' ? b.settledAmount : b.holdAmount) | number: '1.0-0' }}</div>
              <div class="muted small">{{ b.status === 'Completed' || b.status === 'InViolation' ? 'charged' : 'reserved' }}</div>
            </div>
          </a>
        }
      </div>
    }
  `,
  styles: [
    `
      .scope {
        display: flex;
        gap: 8px;
      }

      .tabs {
        display: flex;
        gap: 4px;
        border-bottom: 1px solid var(--hairline);
        margin-bottom: 18px;
      }

      .tabs button {
        height: 44px;
        padding: 0 14px;
        border: 0;
        border-bottom: 2px solid transparent;
        border-radius: 0;
        background: transparent;
        color: var(--ink-muted);
        font-weight: 600;
        margin-bottom: -1px;
      }

      .tabs button:hover:not(:disabled) {
        background: transparent;
        border-color: transparent;
        border-bottom-color: var(--border-strong);
        color: var(--ink);
      }

      .tabs button.on {
        color: var(--ink);
        border-bottom-color: var(--ink);
        font-weight: 700;
      }

      .count {
        margin-left: 6px;
        padding: 2px 7px;
        border-radius: 999px;
        background: var(--sunken);
        font: 700 11px/1.2 var(--font-body);
        color: var(--ink-muted);
      }

      .list {
        display: flex;
        flex-direction: column;
        gap: 12px;
      }

      .item {
        display: flex;
        gap: 16px;
        align-items: center;
        padding: 14px;
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: 18px;
        color: var(--ink);
        text-decoration: none;
        transition: box-shadow 0.18s, border-color 0.18s;
      }

      .item:hover {
        text-decoration: none;
        color: var(--ink);
        box-shadow: 0 6px 20px rgb(28 26 23 / 10%);
        border-color: var(--border-strong);
      }

      .thumb {
        width: 96px;
        height: 80px;
        border-radius: 12px;
        flex: 0 0 auto;
      }

      .body {
        flex: 1 1 auto;
        min-width: 0;
      }

      .top {
        display: flex;
        gap: 10px;
        align-items: center;
        flex-wrap: wrap;
        margin-bottom: 4px;
      }

      .top h3 {
        margin: 0;
      }

      .meta {
        font: 500 14px/1.4 var(--font-body);
      }

      .blocked {
        margin-top: 6px;
        font: 600 13px/1.4 var(--font-body);
        color: var(--warn-ink);
      }

      .money {
        text-align: right;
        flex: 0 0 auto;
      }

      .amt {
        font: 600 20px/1 var(--font-display);
        letter-spacing: -0.02em;
      }

      @media (max-width: 720px) {
        .thumb {
          display: none;
        }

        .tabs button {
          padding: 0 10px;
          font-size: 14px;
        }
      }
    `,
  ],
})
export class BookingsComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly tabs: Array<{ key: Tab; label: string }> = [
    { key: 'upcoming', label: 'Upcoming' },
    { key: 'active', label: 'Active' },
    { key: 'past', label: 'Past' },
  ];

  readonly scope = signal<Scope>('renting');
  readonly tab = signal<Tab>('upcoming');
  readonly bookings = signal<BookingSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly visible = computed(() => this.bookings().filter((b) => tabOf(b) === this.tab()));

  ngOnInit(): void {
    this.load();
  }

  count(tab: Tab): number {
    return this.bookings().filter((b) => tabOf(b) === tab).length;
  }

  setScope(scope: Scope): void {
    if (scope === this.scope()) {
      return;
    }
    this.scope.set(scope);
    this.load();
  }

  when(b: BookingSummary): string {
    const end = new Date(b.actualEndTime ?? b.expectedEndTime)
      .toLocaleTimeString('en-IN', { hour: 'numeric', minute: '2-digit' })
      .toLowerCase();
    return `${describeLocal(b.startTime)} – ${end}`;
  }

  statusLabel(b: BookingSummary): string {
    switch (b.status) {
      case 'Held':
        return 'Reserved';
      case 'Active':
        return 'Parked now';
      case 'Completed':
        return 'Done';
      case 'InViolation':
        return 'Amount owing';
      case 'Disputed':
        return 'Under review';
      case 'Cancelled':
        return 'Cancelled';
    }
  }

  badgeClass(b: BookingSummary): string {
    switch (b.status) {
      case 'Active':
        return 'badge badge--live';
      case 'Held':
        return 'badge badge--info';
      case 'InViolation':
        return 'badge badge--danger';
      case 'Disputed':
        return 'badge badge--warn';
      default:
        return 'badge';
    }
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);

    const request = this.scope() === 'renting' ? this.api.myBookings() : this.api.hostingBookings();
    request.subscribe({
      next: (bookings) => {
        this.bookings.set(
          [...bookings].sort((a, b) => a.startTime.localeCompare(b.startTime) * (tabOf(a) === 'past' ? -1 : 1)),
        );
        // Land on the tab that has something in it.
        if (this.count(this.tab()) === 0) {
          this.tab.set((['active', 'upcoming', 'past'] as Tab[]).find((t) => this.count(t) > 0) ?? 'upcoming');
        }
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }
}

function tabOf(b: BookingSummary): Tab {
  if (b.status === 'Active') {
    return 'active';
  }
  if (b.status === 'Held') {
    return 'upcoming';
  }
  return 'past';
}
