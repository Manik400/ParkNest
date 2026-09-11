import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { ApiService } from '../../core/api.service';
import { KycStatus, KycSubmission } from '../../core/models';
import { StatusPillComponent } from '../../shared/status-pill.component';
import { environment } from '../../../environments/environment';

/**
 * The identity review queue.
 *
 * Approving here is what lets a host turn credits into money, so it is the one screen in this
 * console where a careless click has an irreversible consequence at a bank. Two things are shown
 * next to every row for exactly that reason: whether the account has been refused before, and
 * whether the same document has arrived under another account. Neither is visible from the
 * submission itself, and both are what fraud looks like from here.
 *
 * The document number is deliberately absent — the server stores four characters of it and a
 * keyed hash, and nothing more. Match those four against the photograph.
 */
@Component({
  selector: 'app-kyc',
  standalone: true,
  imports: [DatePipe, FormsModule, StatusPillComponent],
  template: `
    <div class="stack">
      <div>
        <h1>Identity checks</h1>
        <p class="muted">
          Hosts waiting to be verified, oldest first. Until one is approved they can earn credits
          and cannot cash out — which is the whole point of the check, and also why a queue left
          unattended quietly strands people's money.
        </p>
      </div>

      @if (error(); as message) {
        <div class="banner banner--error" role="alert">{{ message }}</div>
      }

      @if (notice(); as message) {
        <div class="banner banner--info" role="status">{{ message }}</div>
      }

      <section class="card stack">
        <div class="row">
          <div>
            <label for="status">Status</label>
            <select id="status" name="status" [(ngModel)]="status" (ngModelChange)="load()">
              <option [ngValue]="'Pending'">Waiting</option>
              <option [ngValue]="'Verified'">Verified</option>
              <option [ngValue]="'Rejected'">Rejected</option>
            </select>
          </div>

          <button type="button" (click)="load()" [disabled]="loading()">Refresh</button>
        </div>

        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (submissions().length === 0) {
          <p class="muted">Nothing waiting.</p>
        } @else {
          <div class="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>Sent</th>
                  <th>Host</th>
                  <th>Document</th>
                  <th>Signals</th>
                  <th>Status</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (submission of submissions(); track submission.id) {
                  <tr>
                    <td>{{ submission.submittedAt | date: 'd MMM, HH:mm' }}</td>
                    <td>
                      {{ submission.legalName }}
                      <div class="muted small">{{ submission.userPhone }}</div>
                    </td>
                    <td>
                      {{ submission.documentType }}
                      <div class="muted small">ends {{ submission.documentLast4 }}</div>
                    </td>
                    <td>
                      @if (submission.previousRejections > 0) {
                        <div class="flag">
                          refused {{ submission.previousRejections }}× before
                        </div>
                      }
                      @if (submission.otherAccountsWithThisDocument > 0) {
                        <div class="flag">
                          same document on {{ submission.otherAccountsWithThisDocument }} other
                          account(s)
                        </div>
                      }
                      @if (
                        submission.previousRejections === 0 &&
                        submission.otherAccountsWithThisDocument === 0
                      ) {
                        <span class="muted small">—</span>
                      }
                    </td>
                    <td><app-status-pill [status]="submission.status" /></td>
                    <td>
                      @if (submission.status === 'Pending') {
                        <button type="button" (click)="select(submission)" [disabled]="busy()">
                          Review
                        </button>
                      } @else if (submission.rejectionReason) {
                        <span class="muted small">{{ submission.rejectionReason }}</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>

      @if (selected(); as submission) {
        <section class="card stack">
          <h2>{{ submission.legalName }}</h2>
          <p class="muted">
            {{ submission.documentType }} ending {{ submission.documentLast4 }}. Payout account
            ends {{ submission.payoutAccountLast4 || '—' }}.
          </p>

          @if (submission.documentPhotoUrl) {
            <a [href]="photo(submission.documentPhotoUrl)" target="_blank" rel="noopener">
              <img class="document" [src]="photo(submission.documentPhotoUrl)" alt="Document" />
            </a>
          } @else {
            <p class="muted">
              No photograph was attached. Refuse unless you have verified this another way — a
              number typed into a form proves only that somebody knows a number.
            </p>
          }

          <div>
            <label for="reason">Reason, if refusing</label>
            <input
              id="reason"
              name="reason"
              [(ngModel)]="reason"
              placeholder="The name does not match the document"
            />
            <small class="muted">
              The host reads this and resubmits against it, so it has to say what to fix.
            </small>
          </div>

          <div class="actions">
            <button class="primary" type="button" (click)="verify()" [disabled]="busy()">
              Verify and open cash-out
            </button>
            <button type="button" (click)="reject()" [disabled]="busy() || !reason.trim()">
              Refuse
            </button>
            <button type="button" (click)="selected.set(null)" [disabled]="busy()">Close</button>
          </div>
        </section>
      }
    </div>
  `,
  styles: [
    `
      .row {
        display: flex;
        align-items: flex-end;
        justify-content: space-between;
        gap: var(--space-3);
        flex-wrap: wrap;
      }

      .flag {
        color: var(--danger, #b3261e);
        font-size: 0.8rem;
        font-weight: 600;
      }

      .document {
        max-width: min(420px, 100%);
        border-radius: 8px;
        border: 1px solid var(--border, #ddd);
      }

      .actions {
        display: flex;
        gap: var(--space-2);
        flex-wrap: wrap;
      }
    `,
  ],
})
export class KycComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly submissions = signal<KycSubmission[]>([]);
  readonly selected = signal<KycSubmission | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);

  status: KycStatus = 'Pending';
  reason = '';

  ngOnInit(): void {
    this.load();
  }

  /** Photos are served from the API host, not from wherever the console happens to be hosted. */
  photo(url: string): string {
    return url.startsWith('http') ? url : `${environment.apiBaseUrl}${url}`;
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.kycQueue(this.status).subscribe({
      next: (submissions) => {
        this.submissions.set(submissions);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.detail ?? 'Could not load the queue.');
        this.loading.set(false);
      },
    });
  }

  select(submission: KycSubmission): void {
    this.selected.set(submission);
    this.reason = '';
    this.notice.set(null);
  }

  verify(): void {
    const submission = this.selected();
    if (!submission) return;

    this.busy.set(true);

    this.api.verifyKyc(submission.id).subscribe({
      next: () => this.done(`${submission.legalName} can now cash out.`),
      error: (err) => this.failed(err),
    });
  }

  reject(): void {
    const submission = this.selected();
    if (!submission) return;

    this.busy.set(true);

    this.api.rejectKyc(submission.id, this.reason.trim()).subscribe({
      next: () => this.done('Refused. The host has been told why.'),
      error: (err) => this.failed(err),
    });
  }

  private done(message: string): void {
    this.busy.set(false);
    this.selected.set(null);
    this.notice.set(message);
    this.load();
  }

  private failed(err: unknown): void {
    this.busy.set(false);
    this.error.set(
      (err as { error?: { detail?: string } })?.error?.detail ?? 'That could not be recorded.',
    );
  }
}
