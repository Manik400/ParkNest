import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** The renter's search as they phrased it: a place and a time, never coordinates. */
export interface SearchState {
  city: string;
  area: string;
  /** Local wall-clock start, "YYYY-MM-DDTHH:mm" as datetime-local writes it. */
  startLocal: string;
  hours: number;
  latitude: number | null;
  longitude: number | null;
}

export interface GeocodeHit {
  latitude: number;
  longitude: number;
  label: string;
}

const STORAGE_KEY = 'parknest.search';

/** Localities offered before the renter types anything. Enough for the pilot cities. */
export const POPULAR_AREAS: Record<string, string[]> = {
  Bengaluru: ['Indiranagar', 'Koramangala', 'MG Road', 'Whitefield'],
  Gurgaon: ['Cyber City', 'Sector 29', 'Golf Course Road', 'MG Road'],
  Mumbai: ['Bandra', 'Andheri', 'Powai', 'Lower Parel'],
  Delhi: ['Connaught Place', 'Hauz Khas', 'Saket', 'Dwarka'],
  Hyderabad: ['Banjara Hills', 'HITEC City', 'Jubilee Hills', 'Gachibowli'],
  Pune: ['Koregaon Park', 'Hinjewadi', 'Kothrud', 'Viman Nagar'],
};

export const CITIES = Object.keys(POPULAR_AREAS);

/**
 * Holds the current search across Home → Explore → Listing, and turns "Indiranagar, Bengaluru"
 * into a point via OpenStreetMap's Nominatim. The geocoder is free and needs no key, which
 * matters as much here as it does for the map tiles; it asks for at most one request a second,
 * and a search box is well inside that.
 */
@Injectable({ providedIn: 'root' })
export class SearchService {
  private readonly http = inject(HttpClient);

  readonly state = signal<SearchState>(load());

  update(patch: Partial<SearchState>): void {
    const next = { ...this.state(), ...patch };
    this.state.set(next);
    try {
      sessionStorage.setItem(STORAGE_KEY, JSON.stringify(next));
    } catch {
      // Private mode or blocked storage: the search still works for this page.
    }
  }

  /** Whole hours after the start, as the API wants it. */
  durationMinutes(): number {
    return Math.max(30, Math.round(this.state().hours * 60));
  }

  startIso(): string {
    return new Date(this.state().startLocal).toISOString();
  }

  async geocode(area: string, city: string): Promise<GeocodeHit | null> {
    const q = [area, city, 'India'].filter((part) => part.trim()).join(', ');
    const params = new HttpParams()
      .set('format', 'jsonv2')
      .set('limit', '1')
      .set('countrycodes', 'in')
      .set('q', q);

    const hits = await firstValueFrom(
      this.http.get<Array<{ lat: string; lon: string; display_name: string }>>(
        'https://nominatim.openstreetmap.org/search',
        { params },
      ),
    );

    const hit = hits[0];
    if (!hit) {
      return null;
    }
    return { latitude: Number(hit.lat), longitude: Number(hit.lon), label: hit.display_name };
  }
}

function load(): SearchState {
  const fallback: SearchState = {
    city: 'Bengaluru',
    area: '',
    startLocal: toLocalInput(nextQuarterHour(new Date(Date.now() + 10 * 60_000))),
    hours: 3,
    latitude: null,
    longitude: null,
  };

  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return fallback;
    }
    const saved = JSON.parse(raw) as SearchState;
    // A start time that has already passed would make every quote fail; roll it forward.
    if (new Date(saved.startLocal).getTime() < Date.now()) {
      saved.startLocal = fallback.startLocal;
    }
    return { ...fallback, ...saved };
  } catch {
    return fallback;
  }
}

function nextQuarterHour(date: Date): Date {
  const d = new Date(date);
  d.setSeconds(0, 0);
  d.setMinutes(Math.ceil(d.getMinutes() / 15) * 15);
  return d;
}

/** datetime-local wants "YYYY-MM-DDTHH:mm" in local time, not an ISO string. */
export function toLocalInput(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return (
    date.getFullYear() +
    '-' + pad(date.getMonth() + 1) +
    '-' + pad(date.getDate()) +
    'T' + pad(date.getHours()) +
    ':' + pad(date.getMinutes())
  );
}

/** "Today 6:00 pm", "Tomorrow 9:30 am", or "Fri 20 Sep, 2:00 pm". */
export function describeLocal(local: string): string {
  const date = new Date(local);
  if (Number.isNaN(date.getTime())) {
    return '';
  }
  const now = new Date();
  const sameDay = (a: Date, b: Date) =>
    a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
  const tomorrow = new Date(now);
  tomorrow.setDate(now.getDate() + 1);

  const time = date.toLocaleTimeString('en-IN', { hour: 'numeric', minute: '2-digit' }).toLowerCase();
  if (sameDay(date, now)) {
    return `Today ${time}`;
  }
  if (sameDay(date, tomorrow)) {
    return `Tomorrow ${time}`;
  }
  return `${date.toLocaleDateString('en-IN', { weekday: 'short', day: 'numeric', month: 'short' })}, ${time}`;
}

/** "400 m away" under a kilometre, "1.2 km away" above it. */
export function describeDistance(metres: number): string {
  if (metres < 950) {
    return `${Math.max(10, Math.round(metres / 10) * 10)} m away`;
  }
  return `${(metres / 1000).toFixed(1)} km away`;
}
