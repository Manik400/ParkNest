import { Component, Input, computed, signal } from '@angular/core';

/**
 * A status badge whose colour reflects whether the state is healthy, needs attention, or is
 * actively bad. `InViolation` is the one that must never read as ordinary — it means a renter
 * left owing credits.
 */
@Component({
  selector: 'app-status-pill',
  standalone: true,
  template: `<span class="pill" [class]="cssClass()">{{ label() }}</span>`,
})
export class StatusPillComponent {
  private readonly value = signal('');

  @Input({ required: true })
  set status(value: string) {
    this.value.set(value ?? '');
  }

  readonly label = computed(() => humanise(this.value()));

  readonly cssClass = computed(() => {
    switch (this.value()) {
      case 'Completed':
      case 'Published':
      case 'Verified':
        return 'pill--good';
      case 'InViolation':
      case 'Disputed':
      case 'Delisted':
        return 'pill--bad';
      case 'Active':
      case 'Held':
      case 'Paused':
      case 'Draft':
        return 'pill--warn';
      default:
        return '';
    }
  });
}

/** "InViolation" reads badly in a table; "In violation" does not. */
function humanise(value: string): string {
  return value.replace(/([a-z])([A-Z])/g, '$1 $2');
}
