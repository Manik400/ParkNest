import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';

import { ApiService } from '../../core/api.service';
import { AnalyticsDay, AnalyticsSummary } from '../../core/models';

/** What the column chart is plotting. One series at a time — never two scales on one axis. */
interface Metric {
  key: string;
  label: string;
  of: (day: AnalyticsDay) => number;
}

const METRICS: Metric[] = [
  { key: 'visits', label: 'Site opens', of: (d) => d.visits },
  { key: 'visitors', label: 'Unique visitors', of: (d) => d.visitors },
  { key: 'pageViews', label: 'Page views', of: (d) => d.pageViews },
  { key: 'bookings', label: 'Bookings', of: (d) => d.bookings },
  { key: 'paymentAttempts', label: 'Payment attempts', of: (d) => d.paymentAttempts },
  { key: 'paymentsSucceeded', label: 'Payments taken', of: (d) => d.paymentsSucceeded },
];

const RANGES = [7, 30, 90] as const;

/** The chart's coordinate space. Scales to whatever width the card gives it. */
const VIEW = { width: 960, height: 260, left: 46, right: 14, top: 14, bottom: 30 };

interface Column {
  date: string;
  value: number;
  x: number;
  y: number;
  width: number;
  height: number;
  path: string;
  /** Percent positions, so an HTML tooltip can sit over a chart that scales with the card. */
  leftPct: number;
  topPct: number;
  bandX: number;
  bandWidth: number;
}

/**
 * The owner's view of what the site is doing: who opened it, who searched, who booked, who paid.
 *
 * Restricted twice over — the admin role, and an email allow-list the API applies on top of it —
 * so this screen is in the menu only for an admin and answers 403 for anyone the list does not
 * name. Nothing here is personal: the counter records a random id a browser made up for itself
 * and never an address, a name or an IP, so a busy day is a number and not a list of people.
 */
@Component({
  selector: 'app-analytics',
  standalone: true,
  imports: [DatePipe, DecimalPipe],
  template: `
    <div class="stack">
      <div class="page-head">
        <h1>Site activity</h1>
        <p class="lede">
          Every visit, search, booking and payment attempt, counted as it happens. No names, no
          addresses, no IPs — a visitor is a random id their own browser made up.
        </p>
      </div>

      @if (error(); as message) {
        <div class="banner banner--error" role="alert">{{ message }}</div>
      }

      <div class="controls">
        <div class="chips" role="group" aria-label="Date range">
          @for (option of ranges; track option) {
            <button
              type="button"
              class="chip"
              [class.selected]="days() === option"
              (click)="setDays(option)"
            >
              {{ option }} days
            </button>
          }
        </div>

        <button type="button" class="ghost sm" (click)="load()" [disabled]="loading()">
          {{ loading() ? 'Loading…' : 'Refresh' }}
        </button>
      </div>

      @if (summary(); as data) {
        <!-- The hero: one number the page leads with, counted since the day the counter was
             switched on rather than over the selected window. -->
        <section class="card hero">
          <div>
            <div class="overline">Times the site has been opened</div>
            <div class="hero-figure">{{ data.allTimeVisits | number }}</div>
            <div class="muted small">
              {{ data.allTimeVisitors | number }}
              {{ data.allTimeVisitors === 1 ? 'browser' : 'browsers' }} ·
              {{ data.allTimePageViews | number }} page views ·
              {{ data.allTimeEvents | number }} events recorded, all time
            </div>
          </div>
        </section>

        <section class="tiles">
          <div class="tile">
            <div class="tile-label">Site opens</div>
            <div class="tile-value">{{ data.totals.visits | number }}</div>
            <div class="tile-note">
              {{ data.totals.visitors | number }} unique
              {{ data.totals.visitors === 1 ? 'visitor' : 'visitors' }}
            </div>
          </div>
          <div class="tile">
            <div class="tile-label">Page views</div>
            <div class="tile-value">{{ data.totals.pageViews | number }}</div>
            <div class="tile-note">{{ data.totals.searches | number }} searches run</div>
          </div>
          <div class="tile">
            <div class="tile-label">Sign-ins</div>
            <div class="tile-value">{{ data.totals.signIns | number }}</div>
            <div class="tile-note">{{ data.totals.signUps | number }} new accounts</div>
          </div>
          <div class="tile">
            <div class="tile-label">Bookings</div>
            <div class="tile-value">{{ data.totals.bookings | number }}</div>
            <div class="tile-note">₹{{ data.totals.bookingValue | number: '1.0-0' }} reserved</div>
          </div>
          <div class="tile">
            <div class="tile-label">Payments started</div>
            <div class="tile-value">{{ data.totals.paymentAttempts | number }}</div>
            <div class="tile-note">
              {{ data.totals.paymentsFailed | number }} failed at the gateway
            </div>
          </div>
          <div class="tile">
            <div class="tile-label">Payments taken</div>
            <div class="tile-value">{{ data.totals.paymentsSucceeded | number }}</div>
            <div class="tile-note">₹{{ data.totals.paymentValue | number: '1.0-0' }} in credits</div>
          </div>
        </section>

        <!-- The one number worth stating outright rather than leaving to be divided in the head:
             how many of the people sent to a gateway came back having paid. -->
        @if (data.totals.paymentAttempts > 0) {
          <section class="card conversion">
            <div class="overline">Payment funnel</div>
            <p>
              <strong>{{ conversion() }}%</strong> of the
              {{ data.totals.paymentAttempts | number }}
              {{ data.totals.paymentAttempts === 1 ? 'checkout' : 'checkouts' }} started in this
              window ended in credits. The rest were abandoned, refused by the gateway, or expired.
            </p>
            <div class="meter" role="img" [attr.aria-label]="conversion() + '% of checkouts paid'">
              <span [style.width.%]="conversion()"></span>
            </div>
          </section>
        }

        <section class="card stack">
          <div class="chart-head">
            <h2>Day by day</h2>

            <div class="chart-controls">
              <select
                class="metric"
                aria-label="What to plot"
                [value]="metric().key"
                (change)="setMetric($any($event.target).value)"
              >
                @for (option of metrics; track option.key) {
                  <option [value]="option.key">{{ option.label }}</option>
                }
              </select>

              <button type="button" class="ghost sm" (click)="asTable.set(!asTable())">
                {{ asTable() ? 'Show chart' : 'Show table' }}
              </button>
            </div>
          </div>

          @if (asTable()) {
            <!-- The same numbers, readable by anything that cannot read a picture. -->
            <div class="table-scroll">
              <table>
                <thead>
                  <tr>
                    <th>Day</th>
                    <th class="numeric">Site opens</th>
                    <th class="numeric">Visitors</th>
                    <th class="numeric">Page views</th>
                    <th class="numeric">Bookings</th>
                    <th class="numeric">Payments started</th>
                    <th class="numeric">Payments taken</th>
                  </tr>
                </thead>
                <tbody>
                  @for (day of reversedDaily(); track day.date) {
                    <tr>
                      <td>{{ day.date | date: 'd MMM y' }}</td>
                      <td class="numeric">{{ day.visits | number }}</td>
                      <td class="numeric">{{ day.visitors | number }}</td>
                      <td class="numeric">{{ day.pageViews | number }}</td>
                      <td class="numeric">{{ day.bookings | number }}</td>
                      <td class="numeric">{{ day.paymentAttempts | number }}</td>
                      <td class="numeric">{{ day.paymentsSucceeded | number }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          } @else {
            <div class="chart">
              <svg
                [attr.viewBox]="'0 0 ' + view.width + ' ' + view.height"
                role="img"
                [attr.aria-label]="chartLabel()"
              >
                <!-- Recessive: hairline, solid, one step off the surface. -->
                @for (tick of ticks(); track tick.value) {
                  <line
                    class="grid"
                    [attr.x1]="view.left"
                    [attr.x2]="view.width - view.right"
                    [attr.y1]="tick.y"
                    [attr.y2]="tick.y"
                  />
                  <text class="tick" [attr.x]="view.left - 10" [attr.y]="tick.y + 4">
                    {{ tick.value | number }}
                  </text>
                }

                @for (column of columns(); track column.date) {
                  @if (column.height > 0) {
                    <path class="bar" [attr.d]="column.path" />
                  }

                  <!-- The hit target, a whole band wide, so a one-pixel column on a quiet day is
                       still something a mouse can find. -->
                  <rect
                    class="hit"
                    [attr.x]="column.bandX"
                    [attr.y]="view.top"
                    [attr.width]="column.bandWidth"
                    [attr.height]="view.height - view.top - view.bottom"
                    (mouseenter)="hovered.set(column)"
                    (mouseleave)="hovered.set(null)"
                  />
                }

                @for (label of axisLabels(); track label.x) {
                  <text class="axis" [attr.x]="label.x" [attr.y]="view.height - 8">
                    {{ label.text }}
                  </text>
                }
              </svg>

              @if (hovered(); as column) {
                <div
                  class="tooltip"
                  [class.tooltip--start]="column.leftPct < 25"
                  [class.tooltip--end]="column.leftPct > 75"
                  [style.left.%]="column.leftPct"
                  [style.top.%]="column.topPct"
                  role="status"
                >
                  <strong>{{ column.value | number }}</strong>
                  <span class="muted small">
                    {{ metric().label }} · {{ column.date | date: 'EEE d MMM' }}
                  </span>
                </div>
              }
            </div>
          }
        </section>

        <div class="split">
          <section class="card stack">
            <h2>Most-opened screens</h2>

            @if (data.topPages.length === 0) {
              <p class="muted">Nothing yet.</p>
            } @else {
              <ul class="pages">
                @for (page of data.topPages; track page.path) {
                  <li>
                    <span class="path">{{ page.path }}</span>
                    <span class="track">
                      <span class="fill" [style.width.%]="share(page.views, data.topPages[0].views)"></span>
                    </span>
                    <span class="count">{{ page.views | number }}</span>
                  </li>
                }
              </ul>
              <p class="muted small">
                An id in a route is folded into <code>:id</code>, so this lists screens rather than
                a line per booking anyone opened.
              </p>
            }
          </section>

          <section class="card stack">
            <h2>Every counter</h2>

            @if (data.counters.length === 0) {
              <p class="muted">Nothing recorded in this window.</p>
            } @else {
              <div class="table-scroll">
                <table>
                  <thead>
                    <tr>
                      <th>Event</th>
                      <th class="numeric">Count</th>
                      <th>Last seen</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (counter of data.counters; track counter.name) {
                      <tr>
                        <td><code>{{ counter.name }}</code></td>
                        <td class="numeric">{{ counter.count | number }}</td>
                        <td>{{ counter.lastSeen | date: 'd MMM, HH:mm' }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </section>
        </div>

        <section class="card stack">
          <h2>Latest</h2>

          @if (data.recent.length === 0) {
            <p class="muted">Nothing recorded in this window.</p>
          } @else {
            <div class="table-scroll">
              <table>
                <thead>
                  <tr>
                    <th>When</th>
                    <th>Event</th>
                    <th>Where</th>
                    <th class="numeric">Amount</th>
                    <th>Note</th>
                  </tr>
                </thead>
                <tbody>
                  @for (event of data.recent; track $index) {
                    <tr>
                      <td>{{ event.occurredAt | date: 'd MMM, HH:mm:ss' }}</td>
                      <td>
                        <code>{{ event.name }}</code>
                        @if (event.signedIn) {
                          <span class="badge badge--info">signed in</span>
                        }
                      </td>
                      <td class="muted">{{ event.path ?? event.source }}</td>
                      <td class="numeric">
                        {{ event.amount == null ? '—' : '₹' + (event.amount | number: '1.0-0') }}
                      </td>
                      <td class="muted">{{ event.detail ?? '—' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>
      } @else if (!loading() && !error()) {
        <div class="empty">
          <div class="empty__mark"></div>
          <h3>Nothing counted yet</h3>
          <p>Open the site in another tab and this page will have something to show.</p>
        </div>
      }
    </div>
  `,
  styles: [
    `
      .controls {
        display: flex;
        align-items: center;
        gap: 12px;
        flex-wrap: wrap;
      }

      .chips {
        display: flex;
        gap: 6px;
      }

      .hero {
        padding: 24px clamp(20px, 3vw, 32px);
      }

      .hero-figure {
        /* Proportional figures: tabular-nums at this size leaves the digits loose. */
        font: 600 clamp(48px, 7vw, 64px) / 1 var(--font-display);
        letter-spacing: -0.04em;
        margin: 6px 0 8px;
      }

      .tiles {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
        gap: 12px;
      }

      .tile {
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: var(--r-card);
        padding: 16px 18px;
      }

      .tile-label {
        font: 800 10px/1 var(--font-body);
        letter-spacing: 0.1em;
        text-transform: uppercase;
        color: var(--ink-muted);
      }

      .tile-value {
        font: 600 30px/1.1 var(--font-display);
        letter-spacing: -0.03em;
        margin: 8px 0 4px;
      }

      .tile-note {
        font-size: 13px;
        color: var(--ink-muted);
      }

      .conversion p {
        max-width: 62ch;
      }

      .meter {
        height: 10px;
        border-radius: 999px;
        background: var(--accent-100);
        overflow: hidden;
      }

      .meter span {
        display: block;
        height: 100%;
        background: var(--accent);
      }

      .chart-head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
        flex-wrap: wrap;
      }

      .chart-head h2 {
        margin: 0;
      }

      .chart-controls {
        display: flex;
        align-items: center;
        gap: 8px;
      }

      .metric {
        width: auto;
        min-width: 180px;
      }

      .chart {
        position: relative;
      }

      .chart svg {
        display: block;
        width: 100%;
        height: auto;
        overflow: visible;
      }

      .grid {
        stroke: var(--hairline);
        stroke-width: 1;
      }

      .bar {
        fill: var(--accent);
      }

      .hit {
        fill: transparent;
      }

      .tick,
      .axis {
        fill: var(--ink-muted);
        font: 500 12px var(--font-body);
        font-variant-numeric: tabular-nums;
      }

      .tick {
        text-anchor: end;
      }

      .axis {
        text-anchor: middle;
      }

      .tooltip {
        position: absolute;
        transform: translate(-50%, -100%);
        margin-top: -10px;
        white-space: nowrap;
        pointer-events: none;
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: 10px;
        box-shadow: var(--shadow-md);
        padding: 8px 10px;
        font: 600 13px/1.3 var(--font-body);
        display: flex;
        flex-direction: column;
      }

      /* Near an edge the tooltip hangs from its near side, so it cannot leave the card however
         narrow the card gets. */
      .tooltip--start {
        transform: translate(0, -100%);
      }

      .tooltip--end {
        transform: translate(-100%, -100%);
      }

      .split {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 16px;
        align-items: start;
      }

      .pages {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 10px;
      }

      .pages li {
        display: grid;
        grid-template-columns: minmax(90px, 1fr) 2fr auto;
        align-items: center;
        gap: 12px;
        font-size: 14px;
      }

      .path {
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
        font-family: ui-monospace, 'SFMono-Regular', Menlo, monospace;
        font-size: 13px;
      }

      .track {
        height: 10px;
        border-radius: 999px;
        background: var(--sunken);
        overflow: hidden;
      }

      .fill {
        display: block;
        height: 100%;
        /* 4px rounded data-end, square where it starts. */
        border-radius: 0 4px 4px 0;
        background: var(--accent);
      }

      .count {
        font-variant-numeric: tabular-nums;
        font-weight: 700;
      }

      code {
        font-family: ui-monospace, 'SFMono-Regular', Menlo, monospace;
        font-size: 12.5px;
      }

      .badge {
        margin-left: 8px;
      }

      @media (max-width: 860px) {
        .split {
          grid-template-columns: 1fr;
        }
      }
    `,
  ],
})
export class AnalyticsComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly ranges = RANGES;
  readonly metrics = METRICS;
  readonly view = VIEW;

  readonly summary = signal<AnalyticsSummary | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly days = signal<number>(30);
  readonly metric = signal<Metric>(METRICS[0]);
  readonly asTable = signal(false);
  readonly hovered = signal<Column | null>(null);

  /** Newest first in the table — the opposite of the chart, which reads left to right. */
  readonly reversedDaily = computed(() => [...(this.summary()?.daily ?? [])].reverse());

  readonly conversion = computed(() => {
    const totals = this.summary()?.totals;

    if (!totals || totals.paymentAttempts === 0) {
      return 0;
    }

    return Math.round((totals.paymentsSucceeded / totals.paymentAttempts) * 100);
  });

  /** The axis maximum, rounded up to something a person would say out loud. */
  readonly scaleMax = computed(() => {
    const peak = Math.max(0, ...(this.summary()?.daily ?? []).map((d) => this.metric().of(d)));
    return peak === 0 ? 1 : niceCeiling(peak);
  });

  readonly ticks = computed(() => {
    const max = this.scaleMax();
    const plotHeight = VIEW.height - VIEW.top - VIEW.bottom;

    return [0, max / 2, max].map((value) => ({
      value,
      y: VIEW.top + plotHeight * (1 - value / max),
    }));
  });

  readonly columns = computed<Column[]>(() => {
    const daily = this.summary()?.daily ?? [];

    if (daily.length === 0) {
      return [];
    }

    const max = this.scaleMax();
    const plotWidth = VIEW.width - VIEW.left - VIEW.right;
    const plotHeight = VIEW.height - VIEW.top - VIEW.bottom;
    const band = plotWidth / daily.length;
    // Capped, and never filling the band: the 2px the gap needs comes out of the slot, and the
    // rest is deliberate air rather than a wider bar.
    const width = Math.max(1, Math.min(24, band - 2));

    return daily.map((day, index) => {
      const value = this.metric().of(day);
      const height = (value / max) * plotHeight;
      const x = VIEW.left + index * band + (band - width) / 2;
      const y = VIEW.top + plotHeight - height;

      return {
        date: day.date,
        value,
        x,
        y,
        width,
        height,
        path: columnPath(x, y, width, height),
        leftPct: ((x + width / 2) / VIEW.width) * 100,
        topPct: (y / VIEW.height) * 100,
        bandX: VIEW.left + index * band,
        bandWidth: band,
      };
    });
  });

  /**
   * A handful of dates along the bottom rather than one per column: at 90 days they would overlap
   * into a smear, and a label that cannot be read is ink pretending to be information.
   */
  readonly axisLabels = computed(() => {
    const columns = this.columns();

    if (columns.length === 0) {
      return [];
    }

    const step = Math.max(1, Math.ceil(columns.length / 8));

    const last = columns.length - 1;

    return columns
      .filter(
        (_, index) =>
          index % step === 0 ||
          // The final date is worth showing, but not on top of the tick before it.
          (index === last && last % step >= step / 2),
      )
      .map((column) => ({
        x: column.bandX + column.bandWidth / 2,
        text: new Date(column.date).toLocaleDateString(undefined, {
          day: 'numeric',
          month: 'short',
        }),
      }));
  });

  readonly chartLabel = computed(() => {
    const total = this.columns().reduce((sum, column) => sum + column.value, 0);
    return `${this.metric().label} per day over ${this.days()} days, ${total} in total.`;
  });

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.analyticsSummary(this.days()).subscribe({
      next: (data) => {
        this.summary.set(data);
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }

  setDays(days: number): void {
    this.days.set(days);
    this.hovered.set(null);
    this.load();
  }

  setMetric(key: string): void {
    this.metric.set(METRICS.find((m) => m.key === key) ?? METRICS[0]);
    this.hovered.set(null);
  }

  share(value: number, of: number): number {
    return of === 0 ? 0 : (value / of) * 100;
  }
}

/** A column with a 4px rounded top and a square foot on the baseline. */
function columnPath(x: number, y: number, width: number, height: number): string {
  const radius = Math.min(4, width / 2, height);
  const bottom = y + height;

  return [
    `M ${x} ${bottom}`,
    `V ${y + radius}`,
    `Q ${x} ${y} ${x + radius} ${y}`,
    `H ${x + width - radius}`,
    `Q ${x + width} ${y} ${x + width} ${y + radius}`,
    `V ${bottom}`,
    'Z',
  ].join(' ');
}

/**
 * 3 → 4, 37 → 50, 412 → 500. Every value here is a count of something, so the ceiling is always
 * even and always whole: a midpoint tick reading "0.5 visits" is not a smaller number, it is a
 * nonsense one.
 */
function niceCeiling(value: number): number {
  if (value <= 10) {
    return Math.max(2, Math.ceil(value / 2) * 2);
  }

  const magnitude = 10 ** Math.floor(Math.log10(value));

  for (const step of [1, 2, 5, 10]) {
    const candidate = step * magnitude;

    if (candidate >= value) {
      return candidate;
    }
  }

  return 10 * magnitude;
}
