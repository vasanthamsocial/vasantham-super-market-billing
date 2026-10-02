import { expect, test, type Page } from '@playwright/test';
import { api, apps, ean13, expectSignedIn, owner, signIn } from '../support/env';

// A regular customer buys on account at the counter, then pays it off in cash; the cash shows in the drawer at close.
test.describe.configure({ mode: 'serial' });

interface Me {
  memberships: { businessId: string }[];
}

interface Product {
  variants: { id: string; units: { id: string }[] }[];
}

/** Set-up through the API: an item at Rs. 52 with stock, and a debtor with Rs. 500 credit for 30 days. */
async function setUp(page: Page, barcode: string, itemName: string, debtorCode: string, debtorName: string) {
  const business = (await api<Me>(page, 'GET', '/api/v1/auth/me')).memberships[0]!.businessId;
  const store = (await api<{ id: string }[]>(page, 'GET', `/api/v1/businesses/${business}/stores`))[0]!.id;
  const pcs = (await api<{ id: string; code: string }[]>(page, 'GET', `/api/v1/businesses/${business}/catalog/units`)).find((u) => u.code === 'PCS')!.id;
  const product = await api<Product>(page, 'POST', `/api/v1/businesses/${business}/catalog/products`, {
    code: `C${barcode.slice(-8)}`,
    name: itemName,
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
  await api(page, 'POST', `/api/v1/businesses/${business}/debtors`, {
    code: debtorCode,
    legalName: debtorName,
    tradeName: null,
    gstin: null,
    stateCode: '33',
    phone: '9845012345',
    creditPeriodDays: 30,
    creditLimit: 500,
  });
}

test('a customer buys on account at the counter and pays it off in cash', async ({ page }) => {
  const stamp = Date.now().toString(36).toUpperCase();
  const barcode = ean13(`891${Date.now()}`.slice(0, 12));
  const itemName = `Good Day Biscuits ${barcode.slice(-4)}`;
  const debtorCode = `CR${stamp}`.slice(0, 10);
  const debtorName = `Selvi Mess ${stamp}`;
  const counterCode = `K${stamp}`.slice(0, 6);

  await signIn(page, apps.billing, owner.username, owner.password);
  await expectSignedIn(page, owner.name);
  await setUp(page, barcode, itemName, debtorCode, debtorName);

  // This browser becomes a counter; the shift opens with Rs. 1,000.
  await page.getByRole('link', { name: 'Counters' }).click();
  await page.getByText('Add a counter').click();
  const create = page.getByTestId('create-counter-form');
  await expect(create.getByLabel('Store')).toContainText('MAIN');
  await create.getByLabel('Counter code').fill(counterCode);
  await create.getByLabel('Counter name').fill('E2E credit counter');
  await create.getByRole('button', { name: 'Add counter' }).click();
  await page.getByTestId('enrol-device-form').getByRole('button', { name: 'Enrol this browser' }).click();
  await expect(page.getByTestId('devices-table')).toContainText('(this browser)');
  await page.getByRole('link', { name: 'Billing (POS)' }).click();
  const openShift = page.getByTestId('open-shift');
  await openShift.getByLabel('Number of Rs. 500', { exact: true }).fill('2');
  await openShift.getByRole('button', { name: 'Open shift with Rs. 1,000.00' }).click();

  // F3: find the customer's account by phone and bill to it.
  const scan = page.getByLabel('Scan or type an item');
  await expect(scan).toBeFocused();
  await scan.press('F3');
  const picker = page.getByTestId('pos-account-picker');
  await picker.getByLabel('Find a customer account').fill('98450');
  await picker.getByRole('button', { name: 'Find' }).click();
  await picker.getByRole('button', { name: new RegExp(debtorName) }).click();
  await expect(page.getByTestId('pos-account')).toContainText(`Account: ${debtorName} - owes Rs. 0.00, credit left Rs. 500.00`);

  // Two packets, all on account.
  await expect(scan).toBeFocused();
  await scan.fill(`2*${barcode}`);
  await scan.press('Enter');
  await expect(page.getByTestId('pos-total')).toContainText('Rs. 104.00');
  await scan.press('F12');
  const pay = page.getByTestId('pos-pay');
  await pay.getByLabel('Cash', { exact: true }).fill('');
  await pay.getByLabel('On account', { exact: true }).fill('104');
  await expect(pay.getByTestId('pay-account')).toContainText('due in 30 days');
  await pay.getByRole('button', { name: 'Complete bill (Enter)' }).click();
  const done = page.getByTestId('pos-done');
  await expect(done.getByTestId('receipt-account')).toContainText(`Rs. 104.00 on account ${debtorCode}, due`);
  await done.getByRole('button', { name: 'New bill (Enter)' }).click();

  // The customer comes back and pays Rs. 104 in cash at the counter.
  await scan.press('F3');
  await picker.getByLabel('Find a customer account').fill(debtorCode);
  await picker.getByRole('button', { name: 'Find' }).click();
  await picker.getByRole('button', { name: new RegExp(`owes Rs. 104.00`) }).click();
  await page.getByRole('button', { name: 'Take payment' }).click();
  const receive = page.getByTestId('pos-receive');
  await receive.getByLabel('Amount received (Rs.)').fill('104');
  await receive.getByRole('button', { name: 'Record payment' }).click();
  await expect(receive.getByRole('status')).toContainText(/Receipt .*\/RCT\/\d{6}: Rs\. 104\.00 received for .*Now owes Rs\. 0\.00/);
  await receive.getByRole('button', { name: 'Close (Esc)' }).click();

  // The drawer: Rs. 1,000 float + Rs. 104 received.
  await page.getByRole('button', { name: 'Close shift' }).click();
  const close = page.getByTestId('pos-close-shift');
  await close.getByLabel('Number of Rs. 500', { exact: true }).fill('2');
  await close.getByLabel('Number of Rs. 100', { exact: true }).fill('1');
  await close.getByLabel('Number of Rs. 2', { exact: true }).fill('2');
  await close.getByRole('button', { name: 'Close shift with Rs. 1,104.00' }).click();
  await expect(close.getByTestId('shift-expected')).toHaveText('1,104.00');
  await expect(close.getByTestId('shift-difference')).toHaveText('None');
});
