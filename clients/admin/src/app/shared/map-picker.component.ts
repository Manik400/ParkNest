import {
  AfterViewInit,
  Component,
  ElementRef,
  EventEmitter,
  Input,
  OnDestroy,
  Output,
  ViewChild,
} from '@angular/core';
import * as L from 'leaflet';

/**
 * A small OpenStreetMap picker. Click or drag the marker to choose a point.
 *
 * Leaflet's default marker icons are resolved from the stylesheet at runtime, which breaks under a
 * bundler; a plain divIcon avoids the whole problem and needs no image assets.
 */
@Component({
  selector: 'app-map-picker',
  standalone: true,
  template: `<div #host class="map" [style.height.px]="height"></div>`,
  styles: [
    `
      .map {
        width: 100%;
        border: 1px solid var(--border);
        border-radius: var(--radius);
        /* Leaflet panes otherwise sit above sticky headers and modals. */
        z-index: 0;
      }
    `,
  ],
})
export class MapPickerComponent implements AfterViewInit, OnDestroy {
  @ViewChild('host', { static: true }) hostEl!: ElementRef<HTMLDivElement>;

  @Input() latitude = 12.9716;
  @Input() longitude = 77.5946;
  @Input() zoom = 15;
  @Input() height = 280;

  /** True on the search screen, where the marker shows where you are looking, not a chosen spot. */
  @Input() readonly = false;

  @Output() readonly pointChanged = new EventEmitter<{ latitude: number; longitude: number }>();

  private map?: L.Map;
  private marker?: L.Marker;

  ngAfterViewInit(): void {
    this.map = L.map(this.hostEl.nativeElement, {
      center: [this.latitude, this.longitude],
      zoom: this.zoom,
      attributionControl: true,
    });

    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19,
      attribution: '&copy; OpenStreetMap contributors',
    }).addTo(this.map);

    this.marker = L.marker([this.latitude, this.longitude], {
      draggable: !this.readonly,
      icon: L.divIcon({
        className: 'parknest-pin',
        html: '<div style="width:18px;height:18px;border-radius:50%;background:#c8622f;border:3px solid #fff;box-shadow:0 0 0 1px rgba(0,0,0,.3)"></div>',
        iconSize: [18, 18],
        iconAnchor: [9, 9],
      }),
    }).addTo(this.map);

    if (!this.readonly) {
      this.marker.on('dragend', () => this.emit(this.marker!.getLatLng()));
      this.map.on('click', (event: L.LeafletMouseEvent) => {
        this.marker!.setLatLng(event.latlng);
        this.emit(event.latlng);
      });
    }

    // The container is often laid out after this runs (inside a card that is still sizing),
    // which leaves Leaflet rendering into a zero-height box until it is told to re-measure.
    setTimeout(() => this.map?.invalidateSize(), 0);
  }

  /** Recentres from outside, e.g. after "use my location". */
  moveTo(latitude: number, longitude: number): void {
    this.latitude = latitude;
    this.longitude = longitude;
    this.marker?.setLatLng([latitude, longitude]);
    this.map?.setView([latitude, longitude], this.map.getZoom());
  }

  ngOnDestroy(): void {
    this.map?.remove();
  }

  private emit(latlng: L.LatLng): void {
    this.latitude = latlng.lat;
    this.longitude = latlng.lng;
    this.pointChanged.emit({ latitude: latlng.lat, longitude: latlng.lng });
  }
}
