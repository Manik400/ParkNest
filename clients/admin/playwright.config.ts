import { defineConfig, devices } from '@playwright/test';

/**
 * End-to-end tests against the real stack: the Angular dev server on 4200 and the API on 7139
 * (Development: sandbox payments, the OTP code shown on the page, the admin phone in
 * appsettings.Development.json). Start the API yourself — `dotnet run --project src/ParkNest.Api
 * --launch-profile https` — the Angular server is started here if it is not already up.
 *
 * `channel: 'chrome'` drives the Chrome already on the machine rather than a downloaded build,
 * which is what makes this runnable on a laptop that cannot reach Playwright's CDN.
 */
export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'e2e-report' }]],
  use: {
    baseURL: process.env['E2E_BASE_URL'] ?? 'http://localhost:4200',
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    channel: 'chrome',
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], channel: 'chrome' } },
    { name: 'phone', use: { ...devices['Pixel 7'], channel: 'chrome' } },
  ],
  webServer: {
    command: 'npm start',
    url: 'http://localhost:4200',
    reuseExistingServer: true,
    timeout: 180_000,
  },
});
