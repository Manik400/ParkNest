import { expect, test } from '@playwright/test';

import {
  ADMIN_PHONE,
  addVehicle,
  apiSignIn,
  book,
  freshPhone,
  publishedSpace,
  recharge,
  useSession,
} from './support';

test.describe('A booking, from reserve to receipt', () => {
  test('reserve, read the money plainly, cancel free, and print a receipt', async ({ page }) => {
    const admin = await apiSignIn(ADMIN_PHONE);
    const host = await apiSignIn(freshPhone());
    const renter = await apiSignIn(freshPhone());

    const spaceId = await publishedSpace(host);
    const vehicleId = await addVehicle(renter);
    await recharge(admin, renter.userId, 1000);

    // Three hours out, so cancelling is inside the free window.
    const bookingId = await book(renter, spaceId, vehicleId, 180, 60);

    await useSession(page, renter);
    await page.goto(`/bookings/${bookingId}`);

    // The strip at the top: state, the one number, and what it means.
    await expect(page.getByText('Reserved', { exact: true })).toBeVisible();
    await expect(page.getByText('Reserved from balance').first()).toBeVisible();
    await expect(page.getByText(/Nothing has been charged yet/)).toBeVisible();
    await expect(page.getByText('₹60').first()).toBeVisible();

    // The trail carries a reference on every transaction.
    await expect(page.locator('code', { hasText: /^TXN-[2-9A-Z]{10}$/ }).first()).toBeVisible();

    await page.getByRole('button', { name: 'Cancel booking' }).click();

    await expect(page.getByText('Cancelled', { exact: true })).toBeVisible();
    await expect(page.getByText(/went back to the balance; nothing was charged/)).toBeVisible();
    // The release names the hold it returns.
    await expect(page.getByText(/reverses/).first()).toBeVisible();

    await page.getByRole('link', { name: 'Receipt' }).click();
    await expect(page).toHaveURL(new RegExp(`/bookings/${bookingId}/receipt`));
    await expect(page.getByRole('heading', { name: 'Breakdown' })).toBeVisible();
    await expect(page.getByText('Outcome')).toBeVisible();
    await expect(page.getByText(/Cancelled in time/)).toBeVisible();
    await expect(page.getByRole('button', { name: /Print/ })).toBeVisible();
  });

  test('the wallet shows a reference on every line and what a return reverses', async ({ page }) => {
    const admin = await apiSignIn(ADMIN_PHONE);
    const host = await apiSignIn(freshPhone());
    const renter = await apiSignIn(freshPhone());

    const spaceId = await publishedSpace(host);
    const vehicleId = await addVehicle(renter);
    await recharge(admin, renter.userId, 500);
    const bookingId = await book(renter, spaceId, vehicleId, 180, 60);

    const api = await (await import('./support')).apiAs(renter);
    const cancelled = await api.post(`/api/bookings/${bookingId}/cancel`);
    expect(cancelled.ok(), await cancelled.text()).toBeTruthy();
    await api.dispose();

    await useSession(page, renter);
    await page.goto('/wallet');

    await expect(page.getByText('Money added')).toBeVisible();
    await expect(page.getByText('Unused time returned').first()).toBeVisible();
    await expect(page.locator('code', { hasText: /^TXN-/ }).first()).toBeVisible();
    await expect(page.getByText(/reverses/).first()).toBeVisible();
  });

  test('a completed session reads as paid, with the host share and the fee', async ({ page }) => {
    const admin = await apiSignIn(ADMIN_PHONE);
    const host = await apiSignIn(freshPhone());
    const renter = await apiSignIn(freshPhone());

    const spaceId = await publishedSpace(host);
    const vehicleId = await addVehicle(renter);
    await recharge(admin, renter.userId, 1000);
    const bookingId = await book(renter, spaceId, vehicleId, 1, 60);

    const { apiAs } = await import('./support');
    const api = await apiAs(renter);
    const started = await api.post(`/api/bookings/${bookingId}/start`, { data: { method: 'AppConfirmed' } });
    expect(started.ok(), await started.text()).toBeTruthy();
    const ended = await api.post(`/api/bookings/${bookingId}/end`, { data: { method: 'AppConfirmed' } });
    expect(ended.ok(), await ended.text()).toBeTruthy();
    await api.dispose();

    await useSession(page, renter);
    await page.goto(`/bookings/${bookingId}`);

    await expect(page.getByText('Paid', { exact: true })).toBeVisible();
    await expect(page.getByText(/Paid in full/)).toBeVisible();
    await expect(page.getByText('Paid to the host')).toBeVisible();
    await expect(page.getByText('ParkNest fee')).toBeVisible();
  });
});
