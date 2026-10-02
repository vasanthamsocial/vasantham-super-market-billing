import { expect, test, type Page } from '@playwright/test';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { api, apps, ean13, expectSignedIn, owner, signIn } from '../support/env';

// Written by scripts/run-counter-agent-e2e.ps1: the agent's pairing token and its "printer".
const authDir = path.resolve(__dirname, '..', '.auth');
const receiptFile = path.join(authDir, 'receipts.bin');

// A manager enrols the counter PC, then bills on it with the keyboard: scan, change quantity, pay cash, get change,
// print and download the invoice; park and retrieve a bill; find the invoice afterwards.
test.describe.configure({ mode: 'serial' });

interface Me {
  memberships: { businessId: string }[];
}

interface Product {
  id: string;
  variants: { id: string; units: { id: string }[] }[];
}

/** Set-up through the API: a product with a barcode, MRP 55, price 52 and 20 in stock. */
async function stockedProduct(page: Page, barcode: string, name: string) {
  const me = await api<Me>(page, 'GET', '/api/v1/auth/me');
  const business = me.memberships[0]!.businessId;
  const store = (await api<{ id: string }[]>(page, 'GET', `/api/v1/businesses/${business}/stores`))[0]!.id;
  const pcs = (await api<{ id: string; code: string }[]>(page, 'GET', `/api/v1/businesses/${business}/catalog/units`)).find((u) => u.code === 'PCS')!.id;
  const product = await api<Product>(page, 'POST', `/api/v1/businesses/${business}/catalog/products`, {
    code: `P${barcode.slice(-8)}`,
    name,
    printName: null,
    categoryId: null,
    brandId: null,
    baseUnitId: pcs,
    hsnSac: '1905',
    supplyType: 'TAXABLE',
    gstRatePercent: 5,
    cessRatePercent: 0,
    isWeighed: false,
    tracksBatches: false,
    tracksExpiry: false,
    tracksSerials: false,
    variant: { code: null, name: null, barcode, mrp: 55 },
  });
  const variant = product.variants[0]!;
  const pack = variant.units[0]!.id;
  await api(page, 'POST', `/api/v1/businesses/${business}/prices/variants/${variant.id}`, {
    variantUnitId: pack,
    rateType: 'STANDARD',
    channel: 'RETAIL',
    price: 52,
    taxInclusive: true,
    mrp: null,
    storeId: null,
    customerGroupId: null,
    membersOnly: false,
    minQuantity: 0,
    maxQuantity: null,
    validFromUtc: null,
    validToUtc: null,
    priority: null,
    note: null,
  });
  await api(page, 'POST', `/api/v1/businesses/${business}/stock/documents`, {
    type: 'OPENING',
    storeId: store,
    targetStoreId: null,
    reason: 'E2E stock',
    note: null,
    idempotencyKey: crypto.randomUUID(),
    negativeStockOverride: false,
    lines: [{ variantId: variant.id, variantUnitId: pack, quantity: 20, unitCost: 40 }],
  });
}

test('a manager enrols a counter and bills on it with the keyboard', async ({ page }) => {
  const barcode = ean13(`890${Date.now()}`.slice(0, 12));
  const name = `Marie Biscuits ${barcode.slice(-4)}`;
  const counterCode = `E${Date.now().toString(36).toUpperCase()}`.slice(0, 6);

  await signIn(page, apps.billing, owner.username, owner.password);
  await expectSignedIn(page, owner.name);
  await stockedProduct(page, barcode, name);

  // Not a counter yet.
  await page.getByRole('link', { name: 'Billing (POS)' }).click();
  await expect(page.getByTestId('pos-not-ready')).toContainText('not enrolled');

  // Enrol this browser as a new counter.
  await page.getByRole('link', { name: 'Counters' }).click();
  await page.getByText('Add a counter').click();
  const create = page.getByTestId('create-counter-form');
  await expect(create.getByLabel('Store')).toContainText('MAIN'); // stores are loaded
  await create.getByLabel('Counter code').fill(counterCode);
  await create.getByLabel('Counter name').fill('E2E front counter');
  await create.getByRole('button', { name: 'Add counter' }).click();
  await expect(page.getByRole('heading', { name: `Counter ${counterCode}: devices` })).toBeVisible();
  await page.getByTestId('enrol-device-form').getByRole('button', { name: 'Enrol this browser' }).click();
  await expect(page.getByTestId('devices-table')).toContainText('(this browser)');

  // Open the shift: count the opening float (Rs. 1000).
  await page.getByRole('link', { name: 'Billing (POS)' }).click();
  const openShift = page.getByTestId('open-shift');
  await openShift.getByLabel('Number of Rs. 500', { exact: true }).fill('2');
  await openShift.getByRole('button', { name: 'Open shift with Rs. 1,000.00' }).click();

  // Bill: scan, set quantity 2 with F4, pay with F12.
  await expect(page.getByTestId('pos-counter')).toHaveText(`Counter ${counterCode}`);
  const scan = page.getByLabel('Scan or type an item');
  await expect(scan).toBeFocused();

  // Connect this counter PC's hardware (the counter agent) with F11.
  await scan.press('F11');
  const hardware = page.getByTestId('pos-hardware');
  await hardware.getByLabel('Pairing token').fill(readFileSync(path.join(authDir, 'agent-token.txt'), 'utf8').trim());
  await hardware.getByRole('button', { name: 'Test connection' }).click();
  await expect(hardware.getByTestId('agent-status')).toContainText('Printer: File; drawer: yes; scale: Simulated');
  await hardware.getByRole('button', { name: 'Save' }).click();
  await expect(scan).toBeFocused();
  await scan.fill(barcode);
  await scan.press('Enter');
  await expect(page.getByTestId('pos-lines')).toContainText(name);
  await expect(page.getByTestId('pos-total')).toContainText('Rs. 52.00');
  await scan.press('F4');
  const quantity = page.getByTestId('pos-quantity').getByLabel('Quantity');
  await quantity.fill('2');
  await quantity.press('Enter');
  await expect(page.getByTestId('pos-total')).toContainText('Rs. 104.00');

  await scan.press('F12');
  const pay = page.getByTestId('pos-pay');
  const cash = pay.getByLabel('Cash', { exact: true });
  await expect(cash).toHaveValue('104.00');
  await cash.fill('200');
  await expect(pay.getByTestId('pay-change')).toHaveText('Change: Rs. 96.00');
  await cash.press('Enter');

  const done = page.getByTestId('pos-done');
  await expect(done.getByTestId('pos-change')).toHaveText('Give change: Rs. 96.00');
  await expect(done.getByTestId('receipt-number')).toHaveText(`${counterCode}-000001`);
  await expect(done.getByTestId('receipt-total')).toHaveText('104.00');
  // The receipt went to the printer through the agent, followed by the drawer pulse (cash was paid).
  await expect.poll(() => (existsSync(receiptFile) ? readFileSync(receiptFile).toString('latin1') : '')).toContain(`No. ${counterCode}-000001`);
  expect(readFileSync(receiptFile).subarray(-5)).toEqual(Buffer.from([0x1b, 0x70, 0, 25, 250]));
  const pdfHref = await done.getByTestId('pos-pdf').getAttribute('href');
  const pdf = await page.request.get(`${apps.billing}${pdfHref}`);
  expect(pdf.headers()['content-type']).toContain('application/pdf');
  expect((await pdf.body()).subarray(0, 5).toString()).toBe('%PDF-');

  // Enter starts the next bill; park it with F8 and bring it back with F9.
  await page.keyboard.press('Enter');
  await expect(scan).toBeFocused();
  await scan.fill(barcode);
  await scan.press('Enter');
  await expect(page.getByTestId('pos-lines')).toContainText(name);
  await scan.press('F8');
  const label = page.getByTestId('pos-park').getByRole('textbox');
  await label.fill('Customer fetching rice');
  await label.press('Enter');
  await expect(page.getByText('Bill parked.')).toBeVisible();
  await expect(page.getByTestId('pos-lines')).not.toContainText(name);
  await scan.press('F9');
  await page.getByTestId('pos-parked').getByRole('button', { name: /Customer fetching rice/ }).click();
  await expect(page.getByTestId('pos-lines')).toContainText(name);

  // The issued bill is in the sales list and can be reprinted.
  await page.getByRole('link', { name: 'Sales invoices' }).click();
  await page.getByLabel('Find an invoice by number or customer').fill(`${counterCode}-000001`);
  await page.getByRole('button', { name: 'Find' }).click();
  await page.getByTestId('invoices-table').getByRole('button', { name: `${counterCode}-000001` }).click();
  await expect(page.getByTestId('invoice-view').getByTestId('receipt-total')).toHaveText('104.00');

  // One of the two packets comes back: F10, find the bill, return 1, refund in cash, credit note PDF.
  await page.getByRole('link', { name: 'Billing (POS)' }).click();
  await expect(scan).toBeFocused();
  await scan.press('F10');
  const ret = page.getByTestId('pos-return');
  await ret.getByLabel('Invoice number').fill(`${counterCode}-000001`);
  await ret.getByRole('button', { name: 'Find invoice' }).click();
  await ret.getByLabel(`Return quantity of ${name}`).fill('1');
  await expect(ret.getByTestId('return-total')).toHaveText('Refund: Rs. 52.00');
  await ret.getByLabel('Reason').fill('Packet torn');
  await ret.getByRole('button', { name: 'Issue credit note' }).click();
  await expect(ret.getByTestId('return-done')).toContainText('Refund Rs. 52.00 (Cash)');
  await expect(ret.getByRole('heading', { name: `Credit note ${counterCode}/CN000001` })).toBeVisible();
  const notePdf = await page.request.get(`${apps.billing}${await ret.getByTestId('return-pdf').getAttribute('href')}`);
  expect(notePdf.headers()['content-type']).toContain('application/pdf');
  await ret.getByRole('button', { name: 'Back to billing' }).click();

  // Close the shift with a blind count: 1000 float + 104 cash sale - 52 cash refund = 1052 in the drawer.
  await page.getByRole('button', { name: 'Close shift' }).click();
  const close = page.getByTestId('pos-close-shift');
  await close.getByLabel('Number of Rs. 500', { exact: true }).fill('2');
  await close.getByLabel('Number of Rs. 50', { exact: true }).fill('1');
  await close.getByLabel('Number of Rs. 2', { exact: true }).fill('1');
  await close.getByRole('button', { name: 'Close shift with Rs. 1,052.00' }).click();
  await expect(close.getByTestId('shift-expected')).toHaveText('1,052.00');
  await expect(close.getByTestId('shift-difference')).toHaveText('None');
  await close.getByRole('button', { name: 'Done' }).click();
  await expect(page.getByTestId('open-shift')).toBeVisible();
});
