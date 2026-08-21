import { Injectable } from '@angular/core';

export interface Coordinates {
  latitude: number;
  longitude: number;
  /** Accuracy radius in metres, as reported by the device. */
  accuracyMetres: number;
}

/**
 * Wraps the browser Geolocation API in a promise with plain-language errors.
 *
 * Geolocation is permission-gated and can simply be denied, so every caller has to cope with not
 * getting a position — this never guesses a fallback location, because silently searching the
 * wrong city is worse than saying "I could not find you".
 */
@Injectable({ providedIn: 'root' })
export class LocationService {
  get isSupported(): boolean {
    return typeof navigator !== 'undefined' && 'geolocation' in navigator;
  }

  current(): Promise<Coordinates> {
    if (!this.isSupported) {
      return Promise.reject(new Error('This browser cannot report your location.'));
    }

    return new Promise((resolve, reject) => {
      navigator.geolocation.getCurrentPosition(
        (position) =>
          resolve({
            latitude: position.coords.latitude,
            longitude: position.coords.longitude,
            accuracyMetres: position.coords.accuracy,
          }),
        (error) => reject(new Error(describe(error))),
        // A stale fix is fine for "roughly where am I"; waiting 10s beats hanging forever.
        { enableHighAccuracy: true, timeout: 10_000, maximumAge: 60_000 },
      );
    });
  }
}

function describe(error: GeolocationPositionError): string {
  switch (error.code) {
    case error.PERMISSION_DENIED:
      return 'Location permission was denied. Allow it in your browser, or type coordinates instead.';
    case error.POSITION_UNAVAILABLE:
      return 'Your position could not be determined. Try again, or type coordinates instead.';
    case error.TIMEOUT:
      return 'Locating you took too long. Try again, or type coordinates instead.';
    default:
      return 'Could not get your location.';
  }
}
