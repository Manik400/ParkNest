import { DecimalPipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { DataResetInfo, DataResetResult, PlatformRevenue, Profile } from '../../core/models';

/**
 * The account as its owner sees it. The sign-in contacts are shown, not edited: attaching an
 * unverified number or address to an account would let whoever owns it sign in as this account.
 * The payment number is the exception — it only ever goes to the payment gateway, which insists
 * on one, so it can be typed here without a verification code.
 */
@Component({
  selector: 'app-profile',
  standalone: true,
  imports: [DecimalPipe, FormsModule],
  template: `
    <div class="stack">
      <div>
        <h1>Profile</h1>
        <p class="muted">Your name, how you sign in, and the number the payment provider uses.</p>
      </div>

      @if (error(); as message) {
        <div class="banner banner--error" role="alert">{{ message }}</div>
      }
      @if (saved()) {
        <div class="banner banner--info">Saved.</div>
      }

      @if (loading()) {
        <p class="muted">Loading…</p>
      }
      @if (profile(); as me) {
        <section class="card stack">
          <h2>Sign-in</h2>
          <dl class="facts">
            <dt>Email</dt>
            <dd>{{ me.email ?? '—' }}</dd>
            <dt>Phone</dt>
            <dd>{{ me.phone ?? '—' }}</dd>
            <dt>Role</dt>
            <dd>{{ me.role }}</dd>
          </dl>
          <small class="muted">
            Sign-in contacts change only by signing in with them, so nobody can attach a number
            they do not hold to your account.
          </small>
        </section>

        <section class="card">
          <form class="stack" (ngSubmit)="save()">
            <div>
              <label for="fullName">Name</label>
              <input id="fullName" name="fullName" [(ngModel)]="fullName" autocomplete="name" />
            </div>

            @if (!me.phone) {
              <div>
                <label for="paymentPhone">Mobile number for payments</label>
                <input
                  id="paymentPhone"
                  name="paymentPhone"
                  type="tel"
                  inputmode="tel"
                  autocomplete="tel"
                  placeholder="10 digits"
                  [(ngModel)]="paymentPhone"
                />
                <small class="muted">
                  The payment provider needs a mobile number on every order. This one is used
                  only for that; it is not a sign-in number. Leave it empty to be asked at checkout.
                </small>
              </div>
            }

            <div class="row">
              <button class="primary" type="submit" [disabled]="busy()">
                {{ busy() ? 'Saving…' : 'Save' }}
              </button>
            </div>
          </form>
        </section>

        @if (auth.isAdmin()) {
          <section class="card stack">
            <div>
              <h2>Platform</h2>
              <p class="muted">What ParkNest itself has kept. Read off the ledger's own account, so there is no second copy of this number to drift.</p>
            </div>
            @if (revenue(); as r) {
              <div class="revenue">
                <div>
                  <div class="overline">Commission kept, all time</div>
                  <div class="big">₹{{ r.total | number: '1.2-2' }}</div>
                </div>
                <div>
                  <div class="overline">This month</div>
                  <div class="big">₹{{ r.thisMonth | number: '1.2-2' }}</div>
                </div>
              </div>
              <p class="muted small">
                ₹{{ r.earned | number: '1.2-2' }} earned across {{ r.settlements }}
                {{ r.settlements === 1 ? 'settlement' : 'settlements' }};
                ₹{{ r.givenBack | number: '1.2-2' }} given back through disputes.
              </p>
            } @else {
              <p class="muted">Loading…</p>
            }
          </section>

          <section class="card stack danger-zone">
            <div>
              <h2>Reset all data</h2>
              @if (resetInfo(); as info) {
                @if (!info.allowed) {
                  <p class="muted">Switched off in this environment (<code>Maintenance:AllowDataReset</code>).</p>
                } @else {
                  <p class="muted">
                    Deletes every booking, wallet, ledger row, listing, payment order, dispute,
                    rating, notification and analytics event. Accounts and price bands survive;
                    every balance goes back to zero. For a pilot on a free database, not for a
                    platform with real money in it.
                  </p>
                }
              }
            </div>

            @if (resetInfo(); as info) {
              @if (info.allowed) {
                @if (resetResult(); as result) {
                  <div class="banner banner--success" role="status">
                    Done. {{ rowsRemoved(result) | number }} rows removed across {{ tableCount(result) }} tables.
                    Kept: {{ result.kept.join(', ') }}.
                  </div>
                }
                <div>
                  <label for="confirm">Type <code>{{ info.confirmationPhrase }}</code> to confirm</label>
                  <input id="confirm" name="confirm" [(ngModel)]="confirmation" autocomplete="off" />
                </div>
                <div class="row">
                  <button
                    type="button"
                    class="danger"
                    [disabled]="busy() || confirmation.trim() !== info.confirmationPhrase"
                    (click)="reset(info.confirmationPhrase)"
                  >
                    {{ busy() ? 'Deleting…' : 'Delete everything except accounts and price bands' }}
                  </button>
                </div>
              }
            }
          </section>
        }
      }
    </div>
  `,
  styles: [
    `
      .facts {
        display: grid;
        grid-template-columns: max-content 1fr;
        gap: var(--space-1) var(--space-4);
        margin: 0;
      }
      .facts dt {
        color: var(--muted);
      }
      .facts dd {
        margin: 0;
      }

      .revenue {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
        gap: 16px;
      }

      .big {
        font: 600 30px/1.1 var(--font-display);
        letter-spacing: -0.03em;
        margin-top: 6px;
      }

      .danger-zone {
        border-color: var(--danger-border);
      }

      code {
        font-family: ui-monospace, 'SFMono-Regular', Menlo, monospace;
        font-size: 12.5px;
      }
    `,
  ],
})
export class ProfileComponent implements OnInit {
  private readonly api = inject(ApiService);
  readonly auth = inject(AuthService);

  readonly revenue = signal<PlatformRevenue | null>(null);
  readonly resetInfo = signal<DataResetInfo | null>(null);
  readonly resetResult = signal<DataResetResult | null>(null);
  confirmation = '';

  readonly profile = signal<Profile | null>(null);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly saved = signal(false);
  readonly error = signal<string | null>(null);

  fullName = '';
  paymentPhone = '';

  rowsRemoved(result: DataResetResult): number {
    return Object.values(result.rowsRemoved).reduce((sum, n) => sum + n, 0);
  }

  tableCount(result: DataResetResult): number {
    return Object.keys(result.rowsRemoved).length;
  }

  reset(phrase: string): void {
    // A second, native confirmation on top of the typed phrase. The phrase proves intent; this
    // catches the Enter key landing on the wrong button.
    if (!confirm('This deletes every booking, wallet and listing on the platform. Continue?')) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.resetResult.set(null);

    this.api.resetData(phrase).subscribe({
      next: (result) => {
        this.busy.set(false);
        this.confirmation = '';
        this.resetResult.set(result);
        this.loadPlatform();
      },
      error: (err: Error) => {
        this.busy.set(false);
        this.error.set(err.message);
      },
    });
  }

  private loadPlatform(): void {
    if (!this.auth.isAdmin()) {
      return;
    }

    this.api.platformRevenue().subscribe({
      next: (revenue) => this.revenue.set(revenue),
      error: (err: Error) => this.error.set(err.message),
    });

    this.api.resetInfo().subscribe({
      next: (info) => this.resetInfo.set(info),
      error: () => this.resetInfo.set({ allowed: false, confirmationPhrase: '' }),
    });
  }

  ngOnInit(): void {
    this.loadPlatform();

    this.api.getProfile().subscribe({
      next: (me) => {
        this.profile.set(me);
        this.fullName = me.fullName;
        this.paymentPhone = me.paymentPhone ?? '';
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }

  save(): void {
    this.busy.set(true);
    this.saved.set(false);
    this.error.set(null);

    // An empty payment number clears it; the server treats null as "leave alone", so send the
    // string either way.
    this.api.updateProfile({ fullName: this.fullName, paymentPhone: this.paymentPhone }).subscribe({
      next: (me) => {
        this.profile.set(me);
        this.paymentPhone = me.paymentPhone ?? '';
        this.busy.set(false);
        this.saved.set(true);
      },
      error: (err: Error) => {
        this.busy.set(false);
        this.error.set(err.message);
      },
    });
  }
}
