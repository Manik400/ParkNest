import {
  AfterViewInit,
  Component,
  ElementRef,
  EventEmitter,
  Input,
  OnChanges,
  OnDestroy,
  Output,
  SimpleChanges,
  ViewChild,
} from '@angular/core';
import * as L from 'leaflet';

import { NearbySpace } from '../core/models';

/**
 * Search results on an OpenStreetMap. Each space is a pill showing only its hourly price — idle
 * white, ink when the card is hovered, accent when selected. The map is read-only: moving it
 * does not re-run the search, so the list and the pins never disagree.
 */
@Component({
  selector: 'app-results-map',
  standalone: true,
  template: `<div #host class="map"></div>`,
  styles: [
    `
      :host {
        display: block;
        height: 100%;
      }

      .map {
        width: 100%;
        height: 100%;
        min-height: 240px;
        background: #e8e2d8;
        z-index: 0;
      }
    `,
  ],
})
export class ResultsMapComponent implements AfterViewInit, OnChanges, OnDestroy {
  @ViewChild('host', { static: true }) hostEl!: ElementRef<HTMLDivElement>;

  @Input() spaces: NearbySpace[] = [];
  @Input() center: { latitude: number; longitude: number } | null = null;
  @Input() hoveredId: string | null = null;
  @Input() selectedId: string | null = null;

  @Output() readonly pinClicked = new EventEmitter<NearbySpace>();

  private map?: L.Map;
  private pins = new Map<string, L.Marker>();
  private origin?: L.CircleMarker;

  ngAfterViewInit(): void {
    this.map = L.map(this.hostEl.nativeElement, {
      center: [this.center?.latitude ?? 12.9716, this.center?.longitude ?? 77.5946],
      zoom: 14,
      zoomControl: true,
      attributionControl: true,
    });

    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19,
      attribution: '&copy; OpenStreetMap contributors',
    }).addTo(this.map);

    this.render();
    setTimeout(() => this.map?.invalidateSize(), 0);
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (!this.map) {
      return;
    }
    if (changes['spaces'] || changes['center']) {
      this.render();
    } else {
      this.restyle();
    }
  }

  /** Leaflet needs a nudge when its container was hidden (the phone's list/map toggle). */
  refresh(): void {
    setTimeout(() => this.map?.invalidateSize(), 0);
  }

  ngOnDestroy(): void {
    this.map?.remove();
  }

  private render(): void {
    if (!this.map) {
      return;
    }

    for (const pin of this.pins.values()) {
      pin.remove();
    }
    this.pins.clear();
    this.origin?.remove();

    for (const space of this.spaces) {
      const marker = L.marker([space.latitude, space.longitude], {
        icon: this.icon(space),
        keyboard: false,
      })
        .on('click', () => this.pinClicked.emit(space))
        .addTo(this.map);
      this.pins.set(space.id, marker);
    }

    if (this.center) {
      this.origin = L.circleMarker([this.center.latitude, this.center.longitude], {
        radius: 7,
        color: '#fff',
        weight: 2,
        fillColor: '#1c1a17',
        fillOpacity: 1,
      }).addTo(this.map);
    }

    const points: L.LatLngExpression[] = this.spaces.map((s) => [s.latitude, s.longitude]);
    if (this.center) {
      points.push([this.center.latitude, this.center.longitude]);
    }
    if (points.length > 1) {
      this.map.fitBounds(L.latLngBounds(points), { padding: [48, 48], maxZoom: 16 });
    } else if (points.length === 1) {
      this.map.setView(points[0], 15);
    }
  }

  private restyle(): void {
    for (const space of this.spaces) {
      this.pins.get(space.id)?.setIcon(this.icon(space));
    }
  }

  private icon(space: NearbySpace): L.DivIcon {
    const state =
      space.id === this.selectedId ? ' is-selected' : space.id === this.hoveredId ? ' is-hot' : '';
    return L.divIcon({
      className: 'parknest-price-pin' + state,
      html: `<div>₹${Math.round(space.pricePerHour)}</div>`,
      iconSize: [0, 0],
      iconAnchor: [0, 0],
    });
  }
}
