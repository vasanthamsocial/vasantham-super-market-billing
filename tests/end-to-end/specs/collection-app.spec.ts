import { expect, test } from '@playwright/test';

test('Collection App renders for a phone and shows live API health', async ({ page }) => {
  await page.goto('http://localhost:3002/');

  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Collections');
  await expect(page.getByTestId('api-status')).toHaveText('Connected', { timeout: 30_000 });
  await expect(page.getByTestId('check-database')).toHaveText('Healthy');

  // Phone layout: no horizontal scrolling at phone width.
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(0);
});
