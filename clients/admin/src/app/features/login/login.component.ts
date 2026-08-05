import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';

import { AuthService } from '../../core/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="page">
      <form class="card stack" (ngSubmit)="submit()">
        <div>
          <h1>ParkNest</h1>
          <p class="muted">Sign in with your phone number.</p>
        </div>

        @if (error(); as message) {
          <div class="banner banner--error" role="alert">{{ message }}</div>
        }

        <div>
          <label for="phone">Phone number</label>
          <input
            id="phone"
            name="phone"
            type="tel"
            autocomplete="tel"
            placeholder="9876543210"
            [(ngModel)]="phone"
            [disabled]="stage() === 'code'"
            required
          />
        </div>

        @if (stage() === 'code') {
          <div>
            <label for="code">Verification code</label>
            <input
              id="code"
              name="code"
              inputmode="numeric"
              autocomplete="one-time-code"
              placeholder="123456"
              [(ngModel)]="code"
              required
            />
          </div>

          <!-- Development convenience: outside Production the API returns the code, because no
               SMS gateway is wired yet. It is null in Production. -->
          @if (devCode(); as dev) {
            <div class="banner banner--info">
              Development code: <strong>{{ dev }}</strong>
            </div>
          }
        }

        <div class="row">
          <button class="primary" type="submit" [disabled]="busy()">
            {{ stage() === 'phone' ? 'Send code' : 'Verify and sign in' }}
          </button>

          @if (stage() === 'code') {
            <button type="button" [disabled]="busy()" (click)="restart()">Use another number</button>
          }
        </div>
      </form>
    </div>
  `,
  styles: [
    `
      .page {
        display: grid;
        place-items: center;
        min-height: 100vh;
        padding: var(--space-4);
      }

      form {
        width: min(380px, 100%);
      }
    `,
  ],
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  phone = '';
  code = '';

  readonly stage = signal<'phone' | 'code'>('phone');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly devCode = signal<string | null>(null);

  submit(): void {
    return this.stage() === 'phone' ? this.sendCode() : this.verify();
  }

  restart(): void {
    this.stage.set('phone');
    this.code = '';
    this.devCode.set(null);
    this.error.set(null);
  }

  private sendCode(): void {
    if (!this.phone.trim()) {
      this.error.set('Enter a phone number.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    this.auth.requestOtp(this.phone).subscribe({
      next: (challenge) => {
        this.busy.set(false);
        this.devCode.set(challenge.devCode);
        this.stage.set('code');
      },
      error: (err: Error) => {
        this.busy.set(false);
        this.error.set(err.message);
      },
    });
  }

  private verify(): void {
    if (!this.code.trim()) {
      this.error.set('Enter the code you received.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    this.auth.verifyOtp(this.phone, this.code).subscribe({
      next: () => {
        this.busy.set(false);
        const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl') ?? '/';
        void this.router.navigateByUrl(returnUrl);
      },
      error: (err: Error) => {
        this.busy.set(false);
        this.error.set(err.message);
      },
    });
  }
}
