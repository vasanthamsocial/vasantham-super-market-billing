import { expect, test } from '@playwright/test';
import { api, apps, expectSignedIn, owner, signIn } from '../support/env';

// The owner opens Reports in the Owner Dashboard after the day's billing (earlier specs bill at the main store), sees
// the sales summary with a totals row that matches the API, groups it by cashier, and exports it as CSV.
test.describe.configure({ mode: 'serial' });

interface Me {
  memberships: { businessId: string }[];
}

interface Report {
  totals: Record<string, unknown>;
  rows: Record<string, unknown>[];
}

const money = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

test('the owner runs the sales summary, groups it by cashier and exports it', async ({ page }) => {
  await signIn(page, apps.owner, owner.username, owner.password);
  await expectSignedIn(page, owner.name);
  await page.getByRole('link', { name: 'Reports' }).click();

  const form = page.getByTestId('report-form');
  await expect(form.getByRole('combobox', { name: 'Report', exact: true })).toHaveValue('sales-summary');
  const from = await form.getByLabel('From', { exact: true }).inputValue();
  const to = await form.getByLabel('To', { exact: true }).inputValue();
  await form.getByRole('button', { name: 'Show report' }).click();

  const business = (await api<Me>(page, 'GET', '/api/v1/auth/me')).memberships[0]!.businessId;
  const expected = await api<Report>(page, 'GET', `/api/v1/businesses/${business}/reports/sales-summary?from=${from}&to=${to}`);
  expect(expected.rows.length).toBeGreaterThan(0);
  const totals = page.getByTestId('report-totals');
  await expect(totals).toContainText('Total');
  await expect(totals).toContainText(money.format(Number(expected.totals.net_sales)));
  await expect(totals).toContainText(String(expected.totals.bills));

  // By cashier: the same totals, one row per cashier.
  await form.getByRole('combobox', { name: 'Group by', exact: true }).selectOption('cashier');
  await form.getByRole('button', { name: 'Show report' }).click();
  await expect(page.getByTestId('report')).toContainText(', by Cashier');
  await expect(page.getByTestId('report-table').getByRole('columnheader', { name: 'Cashier' })).toBeVisible();
  await expect(totals).toContainText(money.format(Number(expected.totals.net_sales)));

  // The CSV has the same columns and the totals row; no horizontal page scroll on a laptop screen.
  const href = await form.getByRole('link', { name: 'Export CSV' }).getAttribute('href');
  expect(href).toContain('format=csv');
  const csv = await (await page.request.get(`${apps.owner}${href}`)).text();
  expect(csv.replace(/^﻿/, '').split(/\r?\n/)[0]).toBe(
    '"Cashier","Bills","Gross","Discounts","Taxable value","CGST","SGST","IGST","Cess","Round-off","Sales","Credit notes","Returns","Net taxable value","Net sales"',
  );
  expect(csv).toContain('"Total"');
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(0);
});
