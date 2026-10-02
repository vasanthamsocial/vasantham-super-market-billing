import { expect, test, type Page } from '@playwright/test';
import { api, apps, expectSignedIn, owner, signIn } from '../support/env';

// The manager puts a debtor on a route, to be visited every week on today's weekday, then the collector (here the
// owner, who may also collect) finds the party on today's list in the Collection App, with what is overdue.
test.describe.configure({ mode: 'serial' });

interface Me {
  memberships: { businessId: string }[];
}

async function debtorOwing(page: Page, code: string, name: string) {
  const business = (await api<Me>(page, 'GET', '/api/v1/auth/me')).memberships[0]!.businessId;
  await api(page, 'POST', `/api/v1/businesses/${business}/debtors`, {
    code,
    legalName: name,
    tradeName: null,
    gstin: null,
    stateCode: '33',
    address: '12 Bazaar Street',
    phone: '9845098450',
    creditPeriodDays: 15,
    creditLimit: 5000,
    openingBalance: 750,
    openingBalanceDate: '2026-09-01',
  });
}

test('a debtor planned on a route for today shows on the collector\'s day with what is overdue', async ({ page }) => {
  const stamp = Date.now().toString(36).toUpperCase();
  const routeCode = `RT${stamp}`.slice(0, 10);
  const debtorCode = `CL${stamp}`.slice(0, 10);
  const debtorName = `Annai Traders ${stamp}`;
  const weekday = new Date().toLocaleDateString('en-IN', { weekday: 'short', timeZone: 'Asia/Kolkata' });

  await signIn(page, apps.billing, owner.username, owner.password);
  await expectSignedIn(page, owner.name);
  await debtorOwing(page, debtorCode, debtorName);

  // A route, and the debtor first on it, every week on today's weekday.
  await page.getByRole('link', { name: 'Collections' }).click();
  const addRoute = page.getByTestId('add-route-form');
  await addRoute.getByLabel('Route code').fill(routeCode);
  await addRoute.getByLabel('Route name').fill('Bazaar round');
  await addRoute.getByRole('button', { name: 'Add route' }).click();
  await expect(page.getByTestId('routes-table')).toContainText('Bazaar round');

  await page.getByLabel('Find a debtor to plan').fill(debtorCode);
  await page.getByLabel('Find a debtor to plan').press('Enter');
  await page.getByRole('button', { name: `${debtorCode} - ${debtorName}` }).click();
  const plan = page.getByTestId('plan-form');
  await plan.getByRole('combobox', { name: 'Route', exact: true }).selectOption({ label: `${routeCode} - Bazaar round` });
  await plan.getByLabel('Visit sequence').fill('1');
  await plan.getByRole('combobox', { name: 'Collector', exact: true }).selectOption({ label: owner.name });
  await plan.getByRole('combobox', { name: 'Visit', exact: true }).selectOption('WEEKDAYS');
  await plan.getByLabel(weekday, { exact: true }).check();
  await plan.getByRole('button', { name: 'Save plan' }).click();
  await expect(page.getByTestId('plans-table')).toContainText(`${debtorCode} - ${debtorName}`);
  await expect(page.getByTestId('plans-table')).toContainText(`Every ${weekday}`);

  // The manager's preview of the owner's day.
  await page.getByRole('combobox', { name: 'Collector', exact: true }).first().selectOption({ label: owner.name });
  await expect(page.getByTestId(`day-party-${debtorCode}`)).toContainText(`${routeCode}-1 ${debtorName}`);

  // The collector's own day in the Collection App (same host, so already signed in: cookies are not per port).
  await page.goto(apps.collection);
  await expectSignedIn(page, owner.name);
  const party = page.getByTestId(`day-party-${debtorCode}`);
  await expect(party).toContainText('Scheduled');
  await expect(party).toContainText('12 Bazaar Street');
  await expect(party).toContainText('Oldest unpaid: Opening balance (due 2026-09-01');
  await expect(party.locator('dd').nth(2)).toHaveText('750.00'); // overdue
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(0);
});
