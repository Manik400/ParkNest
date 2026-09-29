import { Injectable, signal } from '@angular/core';

export type FeedbackKind = 'Problem' | 'Idea' | 'Other';

/**
 * Opens the one feedback dialog, mounted once in the app root, from anywhere: the header, the
 * profile page, the login page. A service rather than a route so the page someone was on — the
 * thing they are reporting — stays behind the dialog and is sent along with it.
 */
@Injectable({ providedIn: 'root' })
export class FeedbackService {
  readonly openAs = signal<FeedbackKind | null>(null);

  open(kind: FeedbackKind = 'Problem'): void {
    this.openAs.set(kind);
  }

  close(): void {
    this.openAs.set(null);
  }
}
