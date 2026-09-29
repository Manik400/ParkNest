import { expect, test } from '@playwright/test';

import {
  ADMIN_PHONE,
  addVehicle,
  apiAs,
  apiSignIn,
  book,
  freshPhone,
  publishedSpace,
  recharge,
  useSession,
} from './support';

test.describe('What only the admin sees', () => {
  test('site activity is there for the admin and not for a renter', async ({ page }) => {
    const admin = await apiSignIn(ADMIN_PHONE);
    await useSession(page, admin);
    await page.goto('/analytics');
    await expect(page.getByRole('heading', { name: 'Site activity' })).toBeVisible();
    await expect(page.getByText('Times the site has been opened')).toBeVisible();

    const renter = await apiSignIn(freshPhone());
    await useSession(page, renter);
    await page.goto('/analytics');
    await expect(page).not.toHaveURL(/\/analytics/);
  });

  test('the profile shows platform revenue and the reset lever, with the phrase gate', async ({ page }) => {
    const admin = await apiSignIn(ADMIN_PHONE);
    await useSession(page, admin);
    await page.goto('/profile');

    await expect(page.getByRole('heading', { name: 'Platform' })).toBeVisible();
    await expect(page.getByText('Commission kept, all time')).toBeVisible();

    await expect(page.getByRole('heading', { name: 'Reset all data' })).toBeVisible();
    const button = page.getByRole('button', { name: /Delete everything/ });
    await expect(button).toBeDisabled();

    await page.locator('input#confirm').fill('reset');
    await expect(button).toBeDisabled();
    // Not pressed: the test proves the gate, not the wipe.
  });

  test('deciding a dispute opens a dialog', async ({ page }) => {
    const admin = await apiSignIn(ADMIN_PHONE);
    const host = await apiSignIn(freshPhone());
    const renter = await apiSignIn(freshPhone());

    const spaceId = await publishedSpace(host);
    const vehicleId = await addVehicle(renter);
    await recharge(admin, renter.userId, 1000);
    const bookingId = await book(renter, spaceId, vehicleId, 1, 60);

    const api = await apiAs(renter);
    await api.post(`/api/bookings/${bookingId}/start`, { data: { method: 'AppConfirmed' } });
    await api.post(`/api/bookings/${bookingId}/end`, { data: { method: 'AppConfirmed' } });
    const raised = await api.post('/api/disputes', {
      data: { bookingId, reason: 'E2E: the space was blocked when I arrived' },
    });
    expect(raised.ok(), await raised.text()).toBeTruthy();
    await api.dispose();

    await useSession(page, admin);
    await page.goto('/disputes');

    const row = page.locator('tr', { hasText: 'E2E: the space was blocked' }).first();
    await row.getByRole('button', { name: 'Decide' }).click();

    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole('heading', { name: 'Decide dispute' })).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Uphold' })).toBeVisible();

    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
  });
});
