import { expect, test } from '@playwright/test';
import { api, apps, expectSignedIn, owner, signIn } from '../support/env';

// The owner opens the Owner Dashboard after the day's billing: the tiles show live figures that match the reports,
// a tile leads to its full report, and when the server cannot be reached the last copy is shown, marked as cached.
test.describe.configure({ mode: 'serial' });

interface Me {
  memberships: { businessId: string }[];
}

interface Dashboard {
  sections: { key: string; metrics: { label: string; value: number | null }[] }[];
}

const money = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

test('the owner sees live tiles that match the reports and a cached copy when the server is unreachable', async ({ page }) => {
  await signIn(page, apps.owner, owner.username, owner.password);
  await expectSignedIn(page, owner.name);

  const dashboard = page.getByTestId('dashboard');
  await expect(dashboard.getByTestId('freshness-badge')).toHaveAttribute('data-freshness', 'live');
  const sales = dashboard.getByTestId('tile-sales-today');
  await expect(sales).toBeVisible();
  for (const key of ['payments-today', 'stock', 'balances', 'collections', 'cash', 'dispatch', 'backup', 'devices']) {
    await expect(dashboard.getByTestId(`tile-${key}`)).toBeVisible();
  }

  const business = (await api<Me>(page, 'GET', '/api/v1/auth/me')).memberships[0]!.businessId;
  const data = await api<Dashboard>(page, 'GET', `/api/v1/businesses/${business}/dashboard`);
  const net = data.sections.find((s) => s.key === 'sales-today')!.metrics.find((m) => m.label === 'Net sales')!.value ?? 0;
  await expect(sales.locator('div', { hasText: 'Net sales' }).locator('dd')).toHaveText(money.format(net));

  // A tile leads to its report, already chosen.
  await sales.getByRole('link', { name: 'Full report' }).click();
  await expect(page.getByTestId('report-form').getByRole('combobox', { name: 'Report', exact: true })).toHaveValue('sales-summary');

  // The server goes away: the copy saved on this computer is shown, marked as cached.
  await page.goto(apps.owner);
  await expect(dashboard.getByTestId('freshness-badge')).toHaveAttribute('data-freshness', 'live');
  await page.route('**/dashboard**', (route) => route.abort());
  await page.reload();
  await expect(dashboard.getByTestId('freshness-badge')).toHaveAttribute('data-freshness', 'cached');
  await expect(dashboard.getByRole('status')).toContainText('The server cannot be reached');
  await expect(sales.locator('div', { hasText: 'Net sales' }).locator('dd')).toHaveText(money.format(net));
  await page.unroute('**/dashboard**');

  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(0);
});
