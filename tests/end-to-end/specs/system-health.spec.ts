import { expect, test, type Page } from '@playwright/test';

// Each app must reach the API through its own origin and show real readiness from PostgreSQL.
async function expectConnected(page: Page) {
  const status = page.getByTestId('system-status');
  await expect(status.getByTestId('api-status')).toHaveText('Connected', { timeout: 30_000 });
  await expect(status.getByTestId('readiness-status')).toHaveText('Healthy');
  await expect(status.getByTestId('check-database')).toHaveText('Healthy');
  await expect(status.getByTestId('check-schema')).toHaveText('Healthy');
  await expect(status.getByTestId('freshness-badge')).toHaveAttribute('data-freshness', 'live');
}

test('Billing Web shows live API and database health', async ({ page }) => {
  await page.goto('http://localhost:3000/');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Billing and Operations');
  await expectConnected(page);
});

test('Owner Dashboard shows live API and database health', async ({ page }) => {
  await page.goto('http://localhost:3001/');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Owner Dashboard');
  await expectConnected(page);
});

test('Owner Archive reports the licensed feature as disabled by default', async ({ page }) => {
  await page.goto('http://localhost:3003/');
  await expectConnected(page);
  await expect(page.getByTestId('archive-disabled')).toBeVisible();
});

test('web apps send security headers and load no third-party resources', async ({ page }) => {
  const externalRequests: string[] = [];
  page.on('request', (request) => {
    const url = new URL(request.url());
    if (url.hostname !== 'localhost') externalRequests.push(request.url());
  });

  const response = await page.goto('http://localhost:3000/');
  await expectConnected(page);

  const headers = response!.headers();
  expect(headers['content-security-policy']).toContain("frame-ancestors 'none'");
  expect(headers['x-content-type-options']).toBe('nosniff');
  expect(headers['x-powered-by']).toBeUndefined();
  expect(externalRequests).toEqual([]);
});
