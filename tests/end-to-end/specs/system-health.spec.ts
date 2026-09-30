import { expect, test, type Page } from '@playwright/test';
import { apps } from '../support/env';

// The sign-in screen shows live server health, so a counter can see whether the store server is reachable.
async function expectConnected(page: Page) {
  const status = page.getByTestId('system-status');
  await expect(status.getByTestId('api-status')).toHaveText('Connected', { timeout: 30_000 });
  await expect(status.getByTestId('readiness-status')).toHaveText('Healthy');
  await expect(status.getByTestId('check-database')).toHaveText('Healthy');
  await expect(status.getByTestId('check-schema')).toHaveText('Healthy');
  await expect(status.getByTestId('freshness-badge')).toHaveAttribute('data-freshness', 'live');
}

test('Billing Web sign-in screen shows live API and database health', async ({ page }) => {
  await page.goto(apps.billing);
  await expect(page.getByTestId('login-form')).toBeVisible();
  await expectConnected(page);
});

test('Owner Dashboard sign-in screen shows live API and database health', async ({ page }) => {
  await page.goto(apps.owner);
  await expect(page.getByTestId('login-form')).toBeVisible();
  await expectConnected(page);
});

test('Owner Archive reports the licensed feature as disabled by default', async ({ page }) => {
  await page.goto(apps.archive);
  await expectConnected(page);
  await expect(page.getByTestId('archive-disabled')).toBeVisible();
});

test('web apps send security headers, hide the session cookie from scripts, and load nothing external', async ({ page }) => {
  const externalRequests: string[] = [];
  page.on('request', (request) => {
    if (new URL(request.url()).hostname !== 'localhost') externalRequests.push(request.url());
  });

  const response = await page.goto(apps.billing);
  await expectConnected(page);

  const headers = response!.headers();
  expect(headers['content-security-policy']).toContain("frame-ancestors 'none'");
  expect(headers['x-content-type-options']).toBe('nosniff');
  expect(headers['x-powered-by']).toBeUndefined();
  expect(externalRequests).toEqual([]);

  // Unauthenticated API calls are refused.
  const api = await page.request.get(`${apps.billing}/api/v1/businesses`);
  expect(api.status()).toBe(401);
});
