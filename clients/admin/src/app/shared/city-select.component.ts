import { Component, ElementRef, HostListener, computed, forwardRef, inject, input, signal } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';

export interface CityOption {
  value: string;
  /** What the list shows; the value when omitted. */
  label?: string;
}

let nextId = 0;

/**
 * A city picker you can type into to filter, but can only leave holding a city from the list.
 * Every city field goes through this rather than a text box, so "gurgaon ", "Gurugram" and
 * "Gurgoan" cannot reach a price band lookup that matches names exactly.
 *
 * `bare` drops the box styling so it sits inside the search bars, which style the host element.
 */
@Component({
  selector: 'app-city-select',
  standalone: true,
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => CitySelectComponent), multi: true }],
  host: { '[class.bare]': 'bare()' },
  template: `
    <input
      [attr.id]="inputId()"
      type="text"
      role="combobox"
      autocomplete="off"
      aria-autocomplete="list"
      [attr.aria-label]="ariaLabel()"
      [attr.aria-expanded]="open()"
      [attr.aria-controls]="listId"
      [attr.aria-activedescendant]="open() && filtered()[active()] ? listId + '-' + active() : null"
      [placeholder]="open() && selectedLabel() ? selectedLabel() : placeholder()"
      [value]="open() ? query() : selectedLabel()"
      [disabled]="disabled()"
      (focus)="openList()"
      (click)="openList()"
      (input)="onType($any($event.target).value)"
      (keydown)="onKey($event)"
    />
    <span class="caret" aria-hidden="true">▾</span>
    @if (open()) {
      <ul class="list" role="listbox" [id]="listId">
        @for (o of filtered(); track o.value; let i = $index) {
          <li
            role="option"
            [id]="listId + '-' + i"
            [class.active]="i === active()"
            [attr.aria-selected]="o.value === value()"
            (mousedown)="$event.preventDefault(); pick(o)"
            (mouseenter)="active.set(i)"
          >
            {{ o.label ?? o.value }}
          </li>
        } @empty {
          <li class="none">{{ emptyText() }}</li>
        }
      </ul>
    }
  `,
  styles: [
    `
      :host {
        display: block;
        position: relative;
        min-width: 0;
      }

      input {
        width: 100%;
        padding-right: 28px;
        cursor: pointer;
        text-overflow: ellipsis;
      }

      :host(.bare) input {
        height: inherit;
        border: 0;
        background: transparent;
        font: inherit;
        color: inherit;
        padding: 0 24px 0 6px;
        border-radius: 8px;
      }

      :host(.bare) input:focus-visible {
        box-shadow: none;
        background: var(--accent-50);
      }

      .caret {
        position: absolute;
        right: 10px;
        top: 50%;
        transform: translateY(-50%);
        pointer-events: none;
        color: var(--ink-muted);
        font-size: 12px;
      }

      .list {
        position: absolute;
        z-index: 1000;
        top: calc(100% + 4px);
        left: 0;
        min-width: 100%;
        max-height: 260px;
        overflow-y: auto;
        margin: 0;
        padding: 4px;
        list-style: none;
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: var(--r-input);
        box-shadow: var(--shadow-md);
        font: 500 14px/1.3 var(--font-body);
        color: var(--ink);
      }

      li {
        padding: 8px 10px;
        border-radius: 8px;
        cursor: pointer;
        white-space: nowrap;
      }

      li.active {
        background: var(--accent-50);
      }

      li[aria-selected='true'] {
        font-weight: 700;
        color: var(--accent-700);
      }

      li.none {
        cursor: default;
        color: var(--ink-muted);
      }
    `,
  ],
})
export class CitySelectComponent implements ControlValueAccessor {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly options = input<Array<CityOption | string>>([]);
  readonly placeholder = input('Select city');
  readonly emptyText = input('No matching city');
  readonly inputId = input<string | null>(null);
  readonly ariaLabel = input<string | null>('City');
  readonly bare = input(false);

  readonly listId = `city-select-${nextId++}`;
  readonly value = signal<string>('');
  readonly query = signal('');
  readonly open = signal(false);
  readonly active = signal(0);
  readonly disabled = signal(false);

  private readonly normalised = computed<CityOption[]>(() =>
    this.options().map((o) => (typeof o === 'string' ? { value: o } : o)),
  );

  readonly filtered = computed(() => {
    const q = this.query().trim().toLowerCase();
    const all = this.normalised();
    return q ? all.filter((o) => (o.label ?? o.value).toLowerCase().includes(q)) : all;
  });

  readonly selectedLabel = computed(() => {
    const hit = this.normalised().find((o) => o.value === this.value());
    return hit ? hit.label ?? hit.value : this.value();
  });

  private onChange: (v: string) => void = () => {};
  private onTouched: () => void = () => {};

  writeValue(v: string | null): void {
    this.value.set(v ?? '');
  }

  registerOnChange(fn: (v: string) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(disabled: boolean): void {
    this.disabled.set(disabled);
  }

  openList(): void {
    if (this.open() || this.disabled()) {
      return;
    }
    this.query.set('');
    const i = this.normalised().findIndex((o) => o.value === this.value());
    this.active.set(Math.max(0, i));
    this.open.set(true);
  }

  onType(text: string): void {
    this.query.set(text);
    this.active.set(0);
    this.open.set(true);
  }

  pick(o: CityOption): void {
    this.close();
    if (o.value !== this.value()) {
      this.value.set(o.value);
      this.onChange(o.value);
    }
  }

  onKey(e: KeyboardEvent): void {
    const list = this.filtered();
    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault();
        if (!this.open()) {
          this.openList();
        } else if (list.length) {
          this.active.set((this.active() + 1) % list.length);
        }
        break;
      case 'ArrowUp':
        e.preventDefault();
        if (list.length) {
          this.active.set((this.active() - 1 + list.length) % list.length);
        }
        break;
      case 'Enter':
        if (this.open()) {
          // Picking a city is not submitting the form around it.
          e.preventDefault();
          const hit = list[this.active()];
          if (hit) {
            this.pick(hit);
          }
        }
        break;
      case 'Escape':
        if (this.open()) {
          e.preventDefault();
          this.close();
        }
        break;
      case 'Tab':
        this.close();
        break;
    }
  }

  @HostListener('document:mousedown', ['$event'])
  onOutside(e: MouseEvent): void {
    if (this.open() && !this.host.nativeElement.contains(e.target as Node)) {
      this.close();
    }
  }

  /** Whatever was typed and not picked is dropped: the field only ever holds a listed city. */
  private close(): void {
    this.open.set(false);
    this.query.set('');
    this.onTouched();
  }
}
