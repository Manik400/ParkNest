import { devices, expect, test } from '@playwright/test';

// The banner reads the browser's user agent, so each case pins one rather than relying on the
// project it happens to run under.
test.describe('Getting the app from a phone browser', () => {
  test.describe('Android', () => {
    test.use({ userAgent: devices['Pixel 7'].userAgent });

    test('offers the APK download, and stays dismissed', async ({ page }) => {
      await page.goto('/login');

      const banner = page.getByRole('complementary', { name: 'Get the ParkNest app' });
      await expect(banner).toContainText('ParkNest for Android');
      await expect(banner.getByRole('link', { name: 'Download' })).toHaveAttribute(
        'href',
        /releases\/download\/android-latest\/parknest\.apk$/,
      );

      await banner.getByRole('button', { name: 'Dismiss' }).click();
      await expect(banner).toBeHidden();
      await page.reload();
      await expect(banner).toBeHidden();
    });
  });

  test.describe('iPhone', () => {
    test.use({ userAgent: devices['iPhone 14'].userAgent });

    test('explains Add to Home Screen instead of a download', async ({ page }) => {
      await page.goto('/login');

      const banner = page.getByRole('complementary', { name: 'Get the ParkNest app' });
      await expect(banner.getByRole('link', { name: 'Download' })).toHaveCount(0);
      await banner.getByRole('button', { name: 'How' }).click();
      await expect(banner).toContainText('Add to Home Screen');
    });
  });

  test.describe('desktop', () => {
    test.use({ userAgent: devices['Desktop Chrome'].userAgent });

    test('shows nothing', async ({ page }) => {
      await page.goto('/login');
      await expect(page.getByRole('complementary', { name: 'Get the ParkNest app' })).toHaveCount(0);
    });
  });
});
