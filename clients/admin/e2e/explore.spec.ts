import { expect, test } from '@playwright/test';

import { BENGALURU, apiSignIn, freshPhone, publishedSpace, useSession } from './support';

test.describe('Finding parking', () => {
  // The browser's own position, so the search needs no geocoder: the OpenStreetMap lookup the
  // typed-area path uses is a third party, and a test that fails when it is slow proves nothing.
  test.use({ geolocation: { ...BENGALURU }, permissions: ['geolocation'] });

  test('a search near a Bengaluru position shows a Bengaluru space, tagged with the right city', async ({ page }) => {
    const host = await apiSignIn(freshPhone());
    const title = `E2E search target ${Date.now()}`;
    await publishedSpace(host, title);

    const renter = await apiSignIn(freshPhone());
    await useSession(page, renter);

    await page.goto('/explore');
    await page.getByRole('button', { name: 'Use my location' }).click();

    await expect(page.getByRole('heading', { name: /near/ })).toBeVisible();

    const card = page.locator('article.result', { hasText: title });
    await expect(card).toBeVisible();
    await expect(card).toContainText('Bengaluru');
    await expect(card).toContainText(/away/);
  });

  test('the selected scope chip keeps its text under the pointer', async ({ page }) => {
    // Screenshot 2: a selected chip turned into a black blank pill on hover.
    const renter = await apiSignIn(freshPhone());
    await useSession(page, renter);
    await page.goto('/bookings');

    const chip = page.getByRole('button', { name: 'My parking' });
    await chip.hover();

    const color = await chip.evaluate((el) => getComputedStyle(el).color);
    const background = await chip.evaluate((el) => getComputedStyle(el).backgroundColor);
    expect(color).not.toBe(background);
    expect(color).toBe('rgb(255, 255, 255)');
  });
});
