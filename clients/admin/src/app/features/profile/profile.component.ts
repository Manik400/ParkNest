import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { ApiService } from '../../core/api.service';
import { Profile } from '../../core/models';

/**
 * The account as its owner sees it. The sign-in contacts are shown, not edited: attaching an
 * unverified number or address to an account would let whoever owns it sign in as this account.
 * The payment number is the exception — it only ever goes to the payment gateway, which insists
 * on one, so it can be typed here without a verification code.
 */
@Component({
  selector: 'app-profile',
  standalone: true,
  imports: [FormsModule],
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
    `,
  ],
})
export class ProfileComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly profile = signal<Profile | null>(null);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly saved = signal(false);
  readonly error = signal<string | null>(null);

  fullName = '';
  paymentPhone = '';

  ngOnInit(): void {
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
