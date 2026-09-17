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
      <div class="side">
        <div class="brand">
          <span class="mark"><span></span></span>
          <span class="wordmark">ParkNest</span>
        </div>
        <h1 class="display">Park in someone's driveway, not three blocks away.</h1>
        <p class="lede">Real spaces from people who live there. Book by the hour, pay from your balance, never hunt for a spot again.</p>
      </div>

      <form class="panel" (ngSubmit)="submit()">
        <h2>{{ stage() === 'phone' ? 'Sign in or join' : 'Check your inbox' }}</h2>
        <p class="hint">
          @if (stage() === 'phone') {
            We'll send a one-time code. No password to remember.
          } @else {
            Enter the 6-digit code we sent to <strong>{{ destination }}</strong>.
          }
        </p>

        @if (error(); as message) {
          <div class="banner banner--error" role="alert">{{ message }}</div>
        }

        <div>
          <label for="destination">Email or phone number</label>
          <input
            id="destination"
            name="destination"
            type="text"
            inputmode="email"
            autocomplete="email"
            autocapitalize="off"
            spellcheck="false"
            placeholder="you@example.com"
            [(ngModel)]="destination"
            [disabled]="stage() === 'code'"
            required
          />
        </div>

        @if (stage() === 'code') {
          <div>
            <label for="code">One-time code</label>
            <input
              id="code"
              name="code"
              class="code"
              inputmode="numeric"
              autocomplete="one-time-code"
              placeholder="123456"
              maxlength="6"
              [(ngModel)]="code"
              required
            />
          </div>

          <!-- Development convenience: the API returns the code when no real email or SMS sender
               is configured. It is always null in Production. -->
          @if (devCode(); as dev) {
            <div class="banner banner--info">
              Development code: <strong>{{ dev }}</strong>
            </div>
          }
        }

        <button class="primary lg block" type="submit" [disabled]="busy()">
          {{ busy() ? 'One moment…' : stage() === 'phone' ? 'Send code' : 'Sign in' }}
        </button>

        @if (stage() === 'code') {
          <button type="button" class="ghost" [disabled]="busy()" (click)="restart()">Use a different email or number</button>
        }

        <p class="fine">By continuing you agree to park where you booked and pay for the time you use.</p>
      </form>
    </div>
  `,
  styles: [
    `
      .page {
        min-height: 100vh;
        display: grid;
        grid-template-columns: 1.1fr 1fr;
        align-items: center;
        gap: clamp(24px, 6vw, 96px);
        padding: clamp(24px, 5vw, 64px);
      }

      .brand {
        display: flex;
        align-items: center;
        gap: 10px;
        margin-bottom: clamp(28px, 5vw, 56px);
      }

      .mark {
        width: 28px;
        height: 28px;
        border-radius: 9px;
        background: var(--accent);
        display: flex;
        align-items: center;
        justify-content: center;
      }

      .mark span {
        width: 10px;
        height: 10px;
        border-radius: 3px;
        background: var(--canvas);
      }

      .wordmark {
        font: 700 19px/1 var(--font-display);
        letter-spacing: -0.02em;
      }

      .display {
        font-size: clamp(30px, 4.5vw, 48px);
        line-height: 1.06;
        letter-spacing: -0.035em;
        max-width: 16ch;
        margin-bottom: 16px;
      }

      .lede {
        font-size: 17px;
        line-height: 1.6;
        color: var(--ink-soft);
        max-width: 44ch;
      }

      .panel {
        width: min(440px, 100%);
        justify-self: center;
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: 24px;
        padding: clamp(22px, 3vw, 32px);
        box-shadow: 0 8px 28px rgb(28 26 23 / 8%);
        display: flex;
        flex-direction: column;
        gap: 18px;
      }

      .panel h2 {
        margin: 0;
      }

      .hint {
        margin: -10px 0 0;
        color: var(--ink-soft);
        font-size: 14px;
      }

      .code {
        font: 600 22px/1 var(--font-display);
        letter-spacing: 0.3em;
        text-align: center;
      }

      .fine {
        margin: 0;
        font-size: 12px;
        line-height: 1.5;
        color: var(--ink-muted);
        text-align: center;
      }

      @media (max-width: 860px) {
        .page {
          grid-template-columns: 1fr;
          align-content: start;
          gap: 28px;
          padding: 24px 16px 40px;
        }

        .brand {
          margin-bottom: 18px;
        }

        .display {
          font-size: 30px;
        }

        .lede {
          display: none;
        }
      }
    `,
  ],
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  /** An email address or a phone number; the API tells them apart. */
  destination = '';
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
    if (!this.destination.trim()) {
      this.error.set('Enter your email address or phone number.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    this.auth.requestOtp(this.destination).subscribe({
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

    this.auth.verifyOtp(this.destination, this.code).subscribe({
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
