import { Component, HostListener, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';

import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { FeedbackKind, FeedbackService } from './feedback.service';

const MIN_LENGTH = 10;
const MAX_LENGTH = 2000;

/**
 * "Report a problem / Send feedback". Mailed straight to the owner with the tester's address as
 * Reply-To, and the page they were on, so a report arrives with what is needed to act on it.
 */
@Component({
  selector: 'app-feedback-dialog',
  standalone: true,
  imports: [FormsModule],
  template: `
    @if (feedback.openAs(); as _) {
      <div class="modal-backdrop" (click)="close()"></div>
      <div class="modal" role="dialog" aria-modal="true" aria-labelledby="feedback-title">
        <section class="card stack modal-card">
          <div class="modal-head">
            <h2 id="feedback-title">{{ sent() ? 'Thank you' : 'Help us improve ParkNest' }}</h2>
            <button type="button" class="ghost sm" aria-label="Close" (click)="close()" [disabled]="busy()">✕</button>
          </div>

          @if (sent()) {
            <p>Your message is on its way to the ParkNest team. If you left an email, we'll reply there.</p>
            <button type="button" class="primary block" (click)="close()">Done</button>
          } @else {
            <p class="muted small">
              ParkNest is in public beta. Every problem report and idea is read, and shapes what gets fixed next.
            </p>

            <form class="stack" (ngSubmit)="send()">
              <div class="kinds" role="radiogroup" aria-label="What is this about?">
                @for (option of kinds; track option.value) {
                  <button
                    type="button"
                    class="chip"
                    role="radio"
                    [class.selected]="kind() === option.value"
                    [attr.aria-checked]="kind() === option.value"
                    (click)="kind.set(option.value)"
                  >
                    {{ option.label }}
                  </button>
                }
              </div>

              <label>
                {{ kind() === 'Problem' ? 'What went wrong?' : kind() === 'Idea' ? 'What would make it better?' : 'Your message' }}
                <textarea
                  name="message"
                  rows="5"
                  [maxlength]="maxLength"
                  [(ngModel)]="message"
                  [placeholder]="
                    kind() === 'Problem'
                      ? 'What you tried, what you expected, and what happened instead.'
                      : 'Tell us what you think.'
                  "
                  required
                ></textarea>
              </label>

              <label>
                Your email {{ auth.isAuthenticated() ? '(optional)' : '' }}
                <input
                  name="email"
                  type="email"
                  autocomplete="email"
                  [(ngModel)]="email"
                  [placeholder]="auth.isAuthenticated() ? 'Leave empty to use your account email' : 'So we can reply'"
                />
              </label>

              @if (error(); as message) {
                <div class="banner banner--error" role="alert">{{ message }}</div>
              }

              <button class="primary block" type="submit" [disabled]="busy()">
                {{ busy() ? 'Sending…' : 'Send' }}
              </button>
            </form>
          }
        </section>
      </div>
    }
  `,
  styles: [
    `
      .modal-backdrop {
        position: fixed;
        inset: 0;
        z-index: 60;
        background: rgb(28 26 23 / 45%);
      }

      .modal {
        position: fixed;
        inset: 0;
        z-index: 61;
        display: flex;
        align-items: flex-start;
        justify-content: center;
        padding: clamp(12px, 6vh, 64px) 16px;
        overflow-y: auto;
        pointer-events: none;
      }

      .modal-card {
        pointer-events: auto;
        width: min(520px, 100%);
        box-shadow: var(--shadow-lg);
      }

      .modal-head {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 12px;
      }

      .modal-head h2 {
        margin: 0;
      }

      .kinds {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }
    `,
  ],
})
export class FeedbackDialogComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  readonly feedback = inject(FeedbackService);
  readonly auth = inject(AuthService);

  readonly kinds: { value: FeedbackKind; label: string }[] = [
    { value: 'Problem', label: 'Report a problem' },
    { value: 'Idea', label: 'Suggest an idea' },
    { value: 'Other', label: 'Other feedback' },
  ];

  readonly maxLength = MAX_LENGTH;
  readonly kind = signal<FeedbackKind>('Problem');
  readonly busy = signal(false);
  readonly sent = signal(false);
  readonly error = signal<string | null>(null);

  message = '';
  email = '';

  constructor() {
    // Each opening starts on the kind it was opened as, and on a fresh form once the last one
    // went. Only the opening is tracked: reading `sent` here would make sending re-run this and
    // wipe the thank-you straight away.
    effect(
      () => {
        const openAs = this.feedback.openAs();
        if (!openAs) {
          return;
        }

        untracked(() => {
          this.kind.set(openAs);
          this.error.set(null);
          if (this.sent()) {
            this.sent.set(false);
            this.message = '';
          }
        });
      },
      { allowSignalWrites: true },
    );
  }

  @HostListener('document:keydown.escape')
  close(): void {
    if (!this.busy()) {
      this.feedback.close();
    }
  }

  send(): void {
    const message = this.message.trim();
    if (message.length < MIN_LENGTH) {
      this.error.set('Tell us a little more — a sentence or two is plenty.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    this.api
      .sendFeedback({
        kind: this.kind(),
        message,
        email: this.email.trim() || null,
        page: this.router.url,
        client: 'web',
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.sent.set(true);
        },
        error: (err: Error) => {
          this.busy.set(false);
          this.error.set(err.message);
        },
      });
  }
}
