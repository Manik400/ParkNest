import { expect, test } from '@playwright/test';

import { freshPhone, signInThroughUi } from './support';

test.describe('Signing in', () => {
  test('a new phone number gets an account and lands on the home page', async ({ page }) => {
    await signInThroughUi(page, freshPhone());

    await expect(page.getByRole('heading', { level: 1 })).toContainText(/driveway/i);
    // The search is the page; the nav link is the desktop bar, which the phone layout hides.
    await expect(page.getByRole('button', { name: 'Find parking' })).toBeVisible();
  });

  test('a visitor who is not signed in is sent to the login page', async ({ page }) => {
    await page.goto('/wallet');

    await expect(page).toHaveURL(/\/login\?returnUrl=%2Fwallet/);
  });

  test('a wrong code is refused without saying why', async ({ page }) => {
    await page.goto('/login');
    await page.getByPlaceholder('you@example.com').fill(freshPhone());
    await page.getByRole('button', { name: 'Send code' }).click();
    await page.getByPlaceholder('123456').fill('000000');
    await page.getByRole('button', { name: 'Sign in' }).click();

    await expect(page.getByRole('alert')).toContainText(/not valid/i);
    await expect(page).toHaveURL(/\/login/);
  });
});
