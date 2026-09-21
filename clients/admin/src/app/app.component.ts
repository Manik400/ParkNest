import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { TelemetryService } from './core/telemetry.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  template: '<router-outlet />',
})
export class AppComponent {
  // Started here rather than in the shell, so a visit counts on the login page too — someone who
  // opened the site and did not sign in is exactly the visitor worth knowing about.
  constructor() {
    inject(TelemetryService).start();
  }
}
