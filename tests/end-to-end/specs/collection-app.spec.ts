import { expect, test } from '@playwright/test';
import { apps, expectSignedIn, owner, signIn } from '../support/env';

test('Collection App signs in on a phone without horizontal scrolling', async ({ page }) => {
  await page.goto(apps.collection);
  await expect(page.getByTestId('login-form')).toBeVisible();
  await expect(page.getByTestId('api-status')).toHaveText('Connected', { timeout: 30_000 });

  await signIn(page, apps.collection, owner.username, owner.password);
  await expectSignedIn(page, owner.name);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Collections');

  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(0);

  // The session cookie is HttpOnly: page scripts cannot read it.
  const visibleCookies = await page.evaluate(() => document.cookie);
  expect(visibleCookies).not.toContain('sb_session');
  expect(visibleCookies).toContain('sb_csrf');
});
