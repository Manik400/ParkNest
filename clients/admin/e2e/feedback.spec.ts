import { Page, expect, test } from '@playwright/test';

import { apiSignIn, freshPhone, useSession } from './support';

/**
 * The request is answered here rather than by the API: locally the API may be wired to a real
 * inbox, and a test run should not mail the owner. The API side is covered by FeedbackTests.
 */
async function captureFeedback(page: Page): Promise<() => Record<string, unknown> | null> {
  let body: Record<string, unknown> | null = null;
  await page.route('**/api/feedback', async (route) => {
    if (route.request().method() === 'POST') {
      body = route.request().postDataJSON();
      await route.fulfill({ status: 204 });
    } else {
      await route.continue();
    }
  });
  return () => body;
}

test.describe('Reporting a problem or sending feedback', () => {
  test('works from the login page, before signing in', async ({ page }) => {
    const sent = await captureFeedback(page);
    await page.goto('/login');

    await page.getByRole('button', { name: 'Trouble signing in? Tell us' }).click();
    const dialog = page.getByRole('dialog', { name: 'Help us improve ParkNest' });
    await expect(dialog.getByRole('radio', { name: 'Report a problem' })).toHaveAttribute('aria-checked', 'true');

    await dialog.getByRole('button', { name: 'Send' }).click();
    await expect(dialog.getByRole('alert')).toContainText('a sentence or two');

    await dialog.getByLabel('What went wrong?').fill('The code email never arrives on my phone.');
    await dialog.getByLabel('Your email').fill('tester@example.com');
    await dialog.getByRole('button', { name: 'Send' }).click();

    await expect(page.getByRole('dialog', { name: 'Thank you' })).toBeVisible();
    expect(sent()).toMatchObject({
      kind: 'Problem',
      message: 'The code email never arrives on my phone.',
      email: 'tester@example.com',
      page: '/login',
      client: 'web',
    });
  });

  test('is one tap away in the header once signed in, and from the profile page', async ({ page }) => {
    const sent = await captureFeedback(page);
    await useSession(page, await apiSignIn(freshPhone()));
    await page.goto('/profile');

    await page.getByRole('button', { name: 'Send feedback' }).click();
    const dialog = page.getByRole('dialog', { name: 'Help us improve ParkNest' });
    await expect(dialog.getByRole('radio', { name: 'Suggest an idea' })).toHaveAttribute('aria-checked', 'true');
    await dialog.getByLabel('What would make it better?').fill('Let me save favourite spots near my office.');
    await dialog.getByRole('button', { name: 'Send' }).click();
    await page.getByRole('button', { name: 'Done' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(sent()).toMatchObject({ kind: 'Idea', page: '/profile', email: null });

    // Opening again starts on a fresh form, not the last thank-you.
    await page.getByRole('button', { name: /Beta\s*Feedback/ }).click();
    await expect(page.getByRole('dialog', { name: 'Help us improve ParkNest' })).toBeVisible();
    await expect(page.getByRole('dialog').locator('textarea')).toHaveValue('');
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });
});
