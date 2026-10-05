import { expect, test, type Page } from '@playwright/test';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { api, apps, ean13, expectSignedIn, owner, signIn } from '../support/env';

// Offline counter billing (D-039): a manager lets this counter PC bill without the server; the server stops answering
// mid-shift; the counter agent issues a real invoice in the counter's offline series and prints it; when the server is
// back the bill is delivered and appears among the store's invoices with that number.
const authDir = path.resolve(__dirname, '..', '.auth');
const receiptFile = path.join(authDir, 'receipts.bin');

interface Me {
  memberships: { businessId: string }[];
}

async function stockedProduct(page: Page, barcode: string, name: string) {
  const business = (await api<Me>(page, 'GET', '/api/v1/auth/me')).memberships[0]!.businessId;
  const store = (await api<{ id: string }[]>(page, 'GET', `/api/v1/businesses/${business}/stores`))[0]!.id;
  const pcs = (await api<{ id: string; code: string }[]>(page, 'GET', `/api/v1/businesses/${business}/catalog/units`)).find((u) => u.code === 'PCS')!.id;
  const product = await api<{ variants: { id: string; units: { id: string }[] }[] }>(page, 'POST', `/api/v1/businesses/${business}/catalog/products`, {
    code: `Q${barcode.slice(-8)}`, name, printName: null, categoryId: null, brandId: null, baseUnitId: pcs, hsnSac: '1905', supplyType: 'TAXABLE',
    gstRatePercent: 5, cessRatePercent: 0, isWeighed: false, tracksBatches: false, tracksExpiry: false, tracksSerials: false,
    variant: { code: null, name: null, barcode, mrp: 55 },
  });
  const variant = product.variants[0]!;
  const pack = variant.units[0]!.id;
  await api(page, 'POST', `/api/v1/businesses/${business}/prices/variants/${variant.id}`, {
    variantUnitId: pack, rateType: 'STANDARD', channel: 'RETAIL', price: 52, taxInclusive: true, mrp: null, storeId: null, customerGroupId: null,
    membersOnly: false, minQuantity: 0, maxQuantity: null, validFromUtc: null, validToUtc: null, priority: null, note: null,
  });
  await api(page, 'POST', `/api/v1/businesses/${business}/stock/documents`, {
    type: 'OPENING', storeId: store, targetStoreId: null, reason: 'E2E stock', note: null, idempotencyKey: crypto.randomUUID(), negativeStockOverride: false,
    lines: [{ variantId: variant.id, variantUnitId: pack, quantity: 20, unitCost: 40 }],
  });
  return { business, store };
}

test('a counter allowed to bill offline issues a real invoice while the server is down and delivers it when it is back', async ({ page }) => {
  test.setTimeout(150_000);
  const barcode = ean13(`891${Date.now()}`.slice(0, 12));
  const name = `Good Day Cookies ${barcode.slice(-4)}`;
  const counterCode = `F${Date.now().toString(36).toUpperCase()}`.slice(0, 5);

  await signIn(page, apps.billing, owner.username, owner.password);
  await expectSignedIn(page, owner.name);
  const { business, store } = await stockedProduct(page, barcode, name);

  // A new counter, this browser enrolled as its PC, and allowed to bill without the server.
  await page.getByRole('link', { name: 'Counters' }).click();
  await page.getByText('Add a counter').click();
  const create = page.getByTestId('create-counter-form');
  await expect(create.getByLabel('Store')).toContainText('MAIN');
  await create.getByLabel('Counter code').fill(counterCode);
  await create.getByLabel('Counter name').fill('E2E offline counter');
  await create.getByRole('button', { name: 'Add counter' }).click();
  await page.getByTestId('enrol-device-form').getByRole('button', { name: 'Enrol this browser' }).click();
  await expect(page.getByTestId('devices-table')).toContainText('(this browser)');
  await page.getByTestId('devices-table').getByRole('button', { name: 'Change' }).click();
  await page.getByTestId('device-offline-form').getByRole('button', { name: 'Save' }).click();
  await expect(page.getByTestId('device-offline')).toContainText('Up to 200 bills');

  // Open the shift and connect the counter agent: it receives the offline price list.
  await page.getByRole('link', { name: 'Billing (POS)' }).click();
  const openShift = page.getByTestId('open-shift');
  await openShift.getByLabel('Number of Rs. 500', { exact: true }).fill('1');
  await openShift.getByRole('button', { name: 'Open shift with Rs. 500.00' }).click();
  const scan = page.getByLabel('Scan or type an item', { exact: true });
  await expect(scan).toBeFocused();
  await scan.press('F11');
  const hardware = page.getByTestId('pos-hardware');
  await hardware.getByLabel('Pairing token').fill(readFileSync(path.join(authDir, 'agent-token.txt'), 'utf8').trim());
  await hardware.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByTestId('offline-ready')).toContainText(`${counterCode}/OF-000001`, { timeout: 30_000 });

  // The server stops answering: scanning switches the counter to offline billing.
  await page.route('**/api/**', (route) => route.abort('internetdisconnected'));
  await scan.fill(barcode);
  await scan.press('Enter');
  const offlinePos = page.getByTestId('offline-pos');
  await expect(page.getByTestId('offline-pos-banner')).toContainText('The store server cannot be reached');
  const offlineScan = page.getByLabel('Scan or type an item (offline)');
  await offlineScan.fill(`2*${barcode}`);
  await offlineScan.press('Enter');
  await expect(offlinePos.getByTestId('offline-lines')).toContainText(name);
  await expect(offlinePos.getByTestId('offline-total')).toContainText('Rs. 104.00');

  // Cash, with change: a real tax invoice in the offline series, printed through the agent.
  await offlinePos.getByRole('button', { name: 'Pay' }).click();
  const payment = offlinePos.getByTestId('offline-payment');
  await payment.getByLabel('Cash (Rs.)').fill('200');
  await payment.getByRole('button', { name: 'Issue bill of Rs. 104.00' }).click();
  const done = page.getByTestId('offline-done');
  await expect(done.getByRole('heading')).toHaveText(`Bill ${counterCode}/OF-000001 issued`);
  await expect(done).toContainText('Give change Rs. 96.00');
  await expect(done.getByTestId('receipt-number')).toHaveText(`${counterCode}/OF-000001`);
  await expect.poll(() => (existsSync(receiptFile) ? readFileSync(receiptFile).toString('latin1') : '')).toContain(`No. ${counterCode}/OF-000001`);
  await done.getByRole('button', { name: 'New bill' }).click();
  await expect(page.getByTestId('offline-pos-banner')).toContainText('1 waiting (Rs. 104.00)');

  // The server answers again: the bill is delivered, and the counter returns to normal billing.
  await page.unroute('**/api/**');
  await expect(page.getByTestId('back-online')).toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('offline-delivered')).toContainText('1 offline bill reached the server');
  await page.getByTestId('back-online').click();
  await expect(page.getByTestId('pos-counter')).toHaveText(`Counter ${counterCode}`);
  await expect(page.getByTestId('offline-ready')).toContainText(`${counterCode}/OF-000002`, { timeout: 30_000 });

  // It is among the store's invoices with its own number and amount.
  const found = await api<{ number: string; grandTotal: number }[]>(page, 'GET',
    `/api/v1/businesses/${business}/sales/invoices?storeId=${store}&search=${encodeURIComponent(`${counterCode}/OF-000001`)}`);
  expect(found.map((f) => [f.number, f.grandTotal])).toEqual([[`${counterCode}/OF-000001`, 104]]);

  // Close the shift (a cashier has one open shift at a time; later tests bill as the owner on other counters).
  await api(page, 'POST', '/api/v1/pos/shift/close', { counts: [], note: 'E2E: closing the offline billing test shift' });
});
