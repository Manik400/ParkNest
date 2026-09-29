import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { TelemetryService } from './core/telemetry.service';
import { FeedbackDialogComponent } from './shared/feedback-dialog.component';
import { GetAppBannerComponent } from './shared/get-app-banner.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, GetAppBannerComponent, FeedbackDialogComponent],
  template: '<app-get-app-banner /><router-outlet /><app-feedback-dialog />',
})
export class AppComponent {
  // Started here rather than in the shell, so a visit counts on the login page too — someone who
  // opened the site and did not sign in is exactly the visitor worth knowing about.
  constructor() {
    inject(TelemetryService).start();
  }
}
