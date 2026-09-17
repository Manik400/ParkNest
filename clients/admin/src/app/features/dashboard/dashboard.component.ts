import { DecimalPipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { ApiService } from '../../core/api.service';
import { BookingSummary } from '../../core/models';
import { CITIES, POPULAR_AREAS, SearchService, describeLocal } from '../../shared/search.service';

/**
 * Home. One search bar, one button. An upcoming or running booking surfaces above everything
 * else, because a renter who is on the way to their car does not want to read a headline.
 */
@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [DecimalPipe, FormsModule, RouterLink],
  template: `
    <section class="hero">
      <h1 class="display">Park in someone's driveway, not three blocks away.</h1>
      <p class="lede">Real spaces from people who live there. Pay by the hour from your ParkNest balance.</p>

      <form class="search" (ngSubmit)="find()">
        <label class="cell">
          <span class="overline">City</span>
          <select name="city" [(ngModel)]="city" (ngModelChange)="area = ''">
            @for (c of cities; track c) {
              <option [value]="c">{{ c }}</option>
            }
          </select>
        </label>
        <label class="cell">
          <span class="overline">Area</span>
          <input name="area" list="areas" placeholder="Indiranagar" autocomplete="off" [(ngModel)]="area" />
          <datalist id="areas">
            @for (a of areas(); track a) {
              <option [value]="a"></option>
            }
          </datalist>
        </label>
        <label class="cell">
          <span class="overline">From</span>
          <input name="from" type="datetime-local" [(ngModel)]="startLocal" required />
        </label>
        <label class="cell">
          <span class="overline">For</span>
          <select name="hours" [(ngModel)]="hours">
            @for (h of hourOptions; track h) {
              <option [ngValue]="h">{{ h }} {{ h === 1 ? 'hour' : 'hours' }}</option>
            }
          </select>
        </label>
        <button type="submit" class="primary lg">Find parking</button>
      </form>

      <div class="popular">
        <span class="muted small">Popular right now</span>
        @for (a of areas(); track a) {
          <button type="button" class="pop" (click)="area = a; find()">{{ a }}</button>
        }
      </div>
    </section>

    @if (upcoming(); as booking) {
      <section class="upcoming">
        <div class="thumb"></div>
        <div class="body">
          <div class="overline accent">{{ booking.status === 'Active' ? 'Parked now' : 'Your upcoming booking' }}</div>
          <div class="title">{{ booking.spaceTitle }}</div>
          <div class="meta">{{ when(booking) }} · {{ booking.spaceAddress }} · ₹{{ booking.holdAmount | number: '1.0-0' }} reserved</div>
        </div>
        <div class="actions">
          <a class="btn dark-ghost" [href]="directions(booking)" target="_blank" rel="noopener">Directions</a>
          <a class="btn light" [routerLink]="['/bookings', booking.id]">View booking</a>
        </div>
      </section>
    }

    <section class="block">
      <h2>Popular areas in {{ city }}</h2>
      <div class="areas">
        @for (a of areas(); track a) {
          <button type="button" class="area-card" (click)="area = a; find()">
            <div class="photo"><span class="photo__hint">street photo<br />{{ a }}</span></div>
            <div class="area-body">
              <div class="area-name">{{ a }}</div>
              <div class="muted small">Find spaces nearby</div>
            </div>
          </button>
        }
      </div>
    </section>

    <section class="block">
      <h2>How it works</h2>
      <div class="steps">
        <div>
          <div class="num">1</div>
          <h3>Search your area and time</h3>
          <p>Pick the locality and the hours you need. You'll see what's free, with the price per hour.</p>
        </div>
        <div>
          <div class="num">2</div>
          <h3>Reserve from your balance</h3>
          <p>The cost of your slot is set aside when you book. Nothing to pay on arrival, no cash, no haggling.</p>
        </div>
        <div>
          <div class="num">3</div>
          <h3>Park, then end the session</h3>
          <p>Leave early and you get the unused hours back. Stay longer and we bill the extra time in 15-minute steps.</p>
        </div>
      </div>
    </section>

    <section class="host-band">
      <div class="host-copy">
        <h2>Earn from your empty space</h2>
        <p>A driveway that sits empty on weekdays can bring in a few thousand rupees a month. Listing takes about five minutes.</p>
        <a routerLink="/listings/new" class="btn primary">List your space</a>
      </div>
      <div class="host-photo"><span class="photo__hint">photo of a host's<br />driveway</span></div>
    </section>
  `,
  styles: [
    `
      .hero {
        padding: clamp(16px, 4vw, 40px) 0 32px;
      }

      .display {
        font-size: clamp(30px, 5.5vw, 52px);
        line-height: 1.06;
        letter-spacing: -0.035em;
        max-width: 18ch;
        margin: 0 0 14px;
      }

      .lede {
        font-size: clamp(15px, 1.6vw, 18px);
        line-height: 1.6;
        color: var(--ink-soft);
        max-width: 52ch;
        margin: 0 0 32px;
      }

      .search {
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: 22px;
        padding: 14px;
        display: grid;
        grid-template-columns: 1.1fr 1.1fr 1.2fr 0.9fr auto;
        gap: 6px;
        align-items: center;
        box-shadow: 0 8px 28px rgb(28 26 23 / 7%);
        max-width: 1040px;
      }

      .cell {
        padding: 6px 12px;
        margin: 0;
        border-left: 1px solid var(--hairline);
        display: flex;
        flex-direction: column;
        gap: 4px;
        min-width: 0;
      }

      .cell:first-child {
        border-left: 0;
      }

      .cell input,
      .cell select {
        width: 100%;
        height: 34px;
        padding: 0 6px;
        border: 0;
        border-radius: 8px;
        background: transparent;
        font: 600 16px/1 var(--font-body);
        color: var(--ink);
      }

      .cell input:focus-visible,
      .cell select:focus-visible {
        box-shadow: none;
        background: var(--accent-50);
      }

      .search button {
        height: 60px;
        padding: 0 32px;
        border-radius: 16px;
      }

      .popular {
        margin-top: 20px;
        display: flex;
        gap: 10px;
        align-items: center;
        flex-wrap: wrap;
      }

      .pop {
        height: 32px;
        padding: 0 14px;
        border-radius: 999px;
        border: 1px solid var(--border);
        font: 600 13px/1 var(--font-body);
      }

      .upcoming {
        background: var(--ink);
        border-radius: 22px;
        padding: 26px 30px;
        display: flex;
        align-items: center;
        gap: 26px;
        flex-wrap: wrap;
        margin-bottom: 44px;
      }

      .thumb {
        width: 96px;
        height: 96px;
        border-radius: 16px;
        background: repeating-linear-gradient(135deg, #3a342e 0 10px, #443d36 10px 20px);
        flex: 0 0 auto;
      }

      .body {
        flex: 1 1 280px;
      }

      .accent {
        color: var(--accent-300);
        margin-bottom: 9px;
      }

      .title {
        font: 600 24px/1.2 var(--font-display);
        color: #fff;
        letter-spacing: -0.02em;
      }

      .meta {
        font: 500 15px/1.5 var(--font-body);
        color: #c9c3ba;
        margin-top: 7px;
      }

      .actions {
        display: flex;
        gap: 10px;
      }

      .dark-ghost {
        background: transparent;
        border-color: #4a443c;
        color: #fff;
      }

      .dark-ghost:hover {
        background: #2a2621;
        border-color: #4a443c;
        color: #fff;
      }

      .light {
        background: #fff;
        border-color: #fff;
      }

      .block {
        margin-bottom: 48px;
      }

      .block h2 {
        font-size: 24px;
        margin-bottom: 20px;
      }

      .areas {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
        gap: 16px;
      }

      .area-card {
        display: block;
        height: auto;
        padding: 0;
        text-align: left;
        border: 1px solid var(--border);
        border-radius: 18px;
        overflow: hidden;
        background: var(--surface);
        transition: box-shadow 0.18s, transform 0.18s;
      }

      .area-card:hover {
        box-shadow: 0 6px 20px rgb(28 26 23 / 10%);
        transform: translateY(-2px);
        border-color: var(--border);
      }

      .area-card .photo {
        aspect-ratio: 3 / 2;
      }

      .area-body {
        padding: 14px 16px 16px;
      }

      .area-name {
        font: 700 16px/1.2 var(--font-body);
        margin-bottom: 5px;
      }

      .steps {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
        gap: 28px;
      }

      .num {
        width: 40px;
        height: 40px;
        border-radius: 12px;
        background: var(--accent-100);
        color: var(--accent-700);
        display: flex;
        align-items: center;
        justify-content: center;
        font: 700 17px/1 var(--font-display);
        margin-bottom: 16px;
      }

      .steps h3 {
        margin-bottom: 7px;
      }

      .steps p {
        color: var(--ink-soft);
        max-width: 36ch;
        margin: 0;
      }

      .host-band {
        background: var(--accent-100);
        border-radius: 22px;
        padding: 36px 40px;
        display: flex;
        align-items: center;
        gap: 36px;
        flex-wrap: wrap;
      }

      .host-copy {
        flex: 1 1 380px;
      }

      .host-copy h2 {
        font-size: 30px;
        color: var(--accent-ink);
        margin-bottom: 12px;
      }

      .host-copy p {
        font-size: 17px;
        line-height: 1.6;
        color: #7a3a18;
        max-width: 44ch;
        margin-bottom: 22px;
      }

      .host-photo {
        flex: 0 0 300px;
        height: 190px;
        border-radius: 16px;
        background: repeating-linear-gradient(135deg, #ead6c7 0 10px, #f0e0d3 10px 20px);
        display: flex;
        align-items: center;
        justify-content: center;
      }

      .host-photo .photo__hint {
        color: #a97b5c;
      }

      @media (max-width: 860px) {
        .search {
          grid-template-columns: 1fr 1fr;
          padding: 6px;
          border-radius: 20px;
        }

        .cell {
          border-left: 0;
          border-top: 1px solid var(--hairline);
          padding: 12px 14px;
        }

        .cell:nth-child(1),
        .cell:nth-child(2) {
          border-top: 0;
        }

        .cell:nth-child(2),
        .cell:nth-child(4) {
          border-left: 1px solid var(--hairline);
        }

        .search button {
          grid-column: 1 / -1;
          height: 52px;
          margin-top: 6px;
        }

        .upcoming {
          padding: 18px;
          gap: 14px;
        }

        .thumb {
          width: 56px;
          height: 56px;
        }

        .title {
          font-size: 17px;
        }

        .meta {
          font-size: 13px;
        }

        .actions {
          width: 100%;
        }

        .actions .btn {
          flex: 1;
        }

        .areas {
          grid-template-columns: 1fr 1fr;
          gap: 12px;
        }

        .host-band {
          padding: 22px;
        }

        .host-copy h2 {
          font-size: 22px;
        }

        .host-copy p {
          font-size: 14px;
        }

        .host-photo {
          display: none;
        }
      }
    `,
  ],
})
export class DashboardComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly search = inject(SearchService);

  readonly cities = CITIES;
  readonly hourOptions = [1, 2, 3, 4, 5, 6, 8, 10, 12];

  city = this.search.state().city;
  area = this.search.state().area;
  startLocal = this.search.state().startLocal;
  hours = this.search.state().hours;

  readonly areas = computed(() => POPULAR_AREAS[this.city] ?? []);
  readonly upcoming = signal<BookingSummary | null>(null);

  ngOnInit(): void {
    this.api.myBookings().subscribe({
      next: (bookings) => {
        // The one that matters next: running now, else the soonest still to start.
        const live = bookings.filter((b) => b.status === 'Active' || b.status === 'Held');
        live.sort((a, b) => (a.status === 'Active' ? -1 : 1) - (b.status === 'Active' ? -1 : 1) || a.startTime.localeCompare(b.startTime));
        this.upcoming.set(live[0] ?? null);
      },
      // A failed lookup here should not stop the search bar from working.
      error: () => this.upcoming.set(null),
    });
  }

  find(): void {
    this.search.update({ city: this.city, area: this.area, startLocal: this.startLocal, hours: this.hours, latitude: null, longitude: null });
    void this.router.navigate(['/explore']);
  }

  when(booking: BookingSummary): string {
    const end = new Date(booking.expectedEndTime).toLocaleTimeString('en-IN', { hour: 'numeric', minute: '2-digit' }).toLowerCase();
    return `${describeLocal(booking.startTime)} – ${end}`;
  }

  directions(booking: BookingSummary): string {
    return `https://www.google.com/maps/dir/?api=1&destination=${encodeURIComponent(booking.spaceAddress)}`;
  }
}
