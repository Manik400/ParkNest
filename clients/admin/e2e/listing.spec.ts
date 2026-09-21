import { expect, test } from '@playwright/test';

import { apiSignIn, freshPhone, useSession } from './support';

test.describe('Listing a space', () => {
  test('the city is a list, the pin follows it, and photos come right after saving', async ({ page }) => {
    const host = await apiSignIn(freshPhone());
    await useSession(page, host);

    await page.goto('/listings/new');

    // A dropdown, not a text box — the fix for "gurgaon" typed over a Bengaluru pin.
    const city = page.locator('select#city');
    await expect(city).toBeVisible();
    await expect(city.locator('option')).toContainText(['Bengaluru', 'Gurgaon', 'Mumbai']);

    await city.selectOption('Pune');
    await expect(page.locator('input#tz')).toHaveValue('Asia/Kolkata');

    await city.selectOption('Bengaluru');
    await page.locator('input#title').fill('E2E covered driveway');
    await page.locator('input#address').fill('12 Main Road, Indiranagar');
    await page.locator('input#price').fill('60');

    // Draft only: publishing depends on a price band existing for the city.
    await page.getByLabel('publish immediately').uncheck();
    await page.getByRole('button', { name: 'Save as draft' }).click();

    // The photo step, on the same page, before the host is sent anywhere.
    await expect(page.getByRole('heading', { name: /now add photos/i })).toBeVisible();
    await expect(page.getByText('+ Add photo')).toBeVisible();

    await page.getByRole('link', { name: 'Done' }).click();
    await expect(page).toHaveURL(/\/listings$/);
    await expect(page.getByText('E2E covered driveway')).toBeVisible();
  });

  test('a city the platform is not in can be asked for', async ({ page }) => {
    const host = await apiSignIn(freshPhone());
    await useSession(page, host);

    await page.goto('/listings/new');
    await page.getByRole('button', { name: /ask for it/i }).click();
    await page.getByPlaceholder('Which city?').fill('Jaipur');
    await page.getByRole('button', { name: 'Send the request' }).click();

    await expect(page.getByText(/Noted — thanks/)).toBeVisible();
  });

  test('the listing page says when you can park in one line when it is always open', async ({ page }) => {
    const host = await apiSignIn(freshPhone());
    const { publishedSpace } = await import('./support');
    const spaceId = await publishedSpace(host, 'E2E always-open bay');

    await useSession(page, host);
    await page.goto(`/spaces/${spaceId}`);

    await expect(page.getByRole('heading', { name: 'When you can park' })).toBeVisible();
    await expect(page.getByText('Open 24 hours, every day')).toBeVisible();
    // The old screen printed "24 hours" once per weekday.
    await expect(page.getByText('24 hours', { exact: true })).toHaveCount(0);
  });
});
