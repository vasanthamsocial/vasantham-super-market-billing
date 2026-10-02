import { expect, test, type Page } from '@playwright/test';
import { api, apps, expectSignedIn, owner, signIn } from '../support/env';

// The owner adds a supplier, orders from them, then receives the goods against the order: the live preview refuses
// more than was ordered, spreads the freight, sets the new selling price, and the scanned invoice is attached.
test.describe.configure({ mode: 'serial' });

interface Me {
  memberships: { businessId: string }[];
}

/** Set-up through the API: an item with MRP 60 and no stock. */
async function product(page: Page, code: string, name: string) {
  const me = await api<Me>(page, 'GET', '/api/v1/auth/me');
  const business = me.memberships[0]!.businessId;
  const pcs = (await api<{ id: string; code: string }[]>(page, 'GET', `/api/v1/businesses/${business}/catalog/units`)).find((u) => u.code === 'PCS')!.id;
  await api(page, 'POST', `/api/v1/businesses/${business}/catalog/products`, {
    code,
    name,
    printName: null,
    categoryId: null,
    brandId: null,
    baseUnitId: pcs,
    hsnSac: '1006',
    supplyType: 'TAXABLE',
    gstRatePercent: 5,
    cessRatePercent: 0,
    isWeighed: false,
    tracksBatches: false,
    tracksExpiry: false,
    tracksSerials: false,
    variant: { code: null, name: null, barcode: null, mrp: 60 },
  });
}

test('owner orders from a supplier and receives the goods against the order with freight, a new price and the scanned invoice', async ({ page }) => {
  const stamp = Date.now().toString(36).toUpperCase();
  const code = `RICE${stamp}`.slice(0, 12);
  const name = `Ponni Rice 1 kg ${code}`;
  const supplierCode = `SUP${stamp}`.slice(0, 10);
  const supplierName = `Kaveri Traders ${stamp}`;

  await signIn(page, apps.billing, owner.username, owner.password);
  await expectSignedIn(page, owner.name);
  await product(page, code, name);

  // A supplier without a GSTIN.
  await page.getByRole('link', { name: 'Suppliers' }).click();
  const add = page.getByTestId('add-supplier-form');
  await add.getByLabel('Code', { exact: true }).fill(supplierCode);
  await add.getByLabel('Name', { exact: true }).fill(supplierName);
  await add.getByLabel('GST state code').fill('33');
  await add.getByRole('button', { name: 'Add supplier' }).click();
  await expect(page.getByTestId('suppliers-table')).toContainText(supplierName);

  // Order 10 at Rs. 40.
  await page.getByRole('link', { name: 'Purchase orders' }).click();
  const order = page.getByTestId('order-form');
  await order.getByRole('combobox', { name: 'Supplier', exact: true }).selectOption({ label: `${supplierCode} - ${supplierName} (unregistered)` });
  await order.getByLabel('Find an item to add').fill(code);
  await order.getByRole('button', { name: 'Find item' }).click();
  await order.getByRole('button', { name: `Add ${code} - ${name}` }).click();
  await order.getByLabel(`Order quantity of ${name}`).fill('10');
  await order.getByLabel(`Agreed rate of ${name}`).fill('40');
  await order.getByRole('button', { name: 'Place order' }).click();
  await expect(page.getByRole('status').filter({ hasText: /Placed order .*\/PO\/\d{6}/ })).toBeVisible();

  // Receive against the order: 12 is more than ordered, so the preview refuses it.
  await page.getByRole('link', { name: 'Receive goods' }).click();
  const grn = page.getByTestId('grn-form');
  await grn.getByRole('combobox', { name: 'Supplier', exact: true }).selectOption({ label: `${supplierCode} - ${supplierName} (unregistered)` });
  await expect(grn.getByLabel('Document')).toHaveValue('UNREGISTERED');
  await grn.getByLabel('Supplier invoice number').fill(`KT-${stamp}`);
  await grn.getByLabel('Against order').selectOption({ index: 1 });
  await grn.getByRole('button', { name: 'Fill outstanding items' }).click();
  await expect(grn.getByLabel(`Quantity of ${name}`, { exact: true })).toHaveValue('10');
  await expect(grn.getByLabel(`Rate of ${name}`, { exact: true })).toHaveValue('40');

  await grn.getByLabel(`Quantity of ${name}`, { exact: true }).fill('12');
  await expect(grn.getByTestId('grn-line-result-1')).toContainText('only 10 of 10 is outstanding');
  await expect(grn.getByRole('button', { name: 'Save goods receipt' })).toBeDisabled();

  // 10 with Rs. 50 freight: landed Rs. 450, Rs. 45 a unit; sell at Rs. 58 from now on.
  await grn.getByLabel(`Quantity of ${name}`, { exact: true }).fill('10');
  await grn.getByRole('button', { name: 'Add expense' }).click();
  await grn.getByLabel('Expense amount').fill('50');
  await grn.getByLabel(`Selling price of ${name}`, { exact: true }).fill('58');
  await grn.getByLabel('Set as new price').check();
  await expect(grn.getByTestId('grn-totals')).toContainText('450.00');
  await expect(grn.getByTestId('grn-line-result-1')).toContainText('Rs. 45.0000 per stock unit');
  await grn.getByRole('button', { name: 'Save goods receipt' }).click();
  await expect(page.getByRole('status').filter({ hasText: /Goods receipt .*\/GRN\/\d{6} is posted/ })).toBeVisible();

  // Attach the scanned supplier invoice.
  const attachments = page.getByTestId('grn-attachments');
  await attachments.getByLabel('File to attach').setInputFiles({
    name: 'kaveri-invoice.pdf',
    mimeType: 'application/pdf',
    buffer: Buffer.from('%PDF-1.4\n% scanned invoice\n%%EOF\n'),
  });
  await attachments.getByRole('button', { name: 'Attach file' }).click();
  await expect(attachments.getByRole('link', { name: 'kaveri-invoice.pdf' })).toBeVisible();

  // A file that only pretends to be a PDF is refused.
  await attachments.getByLabel('File to attach').setInputFiles({ name: 'fake.pdf', mimeType: 'application/pdf', buffer: Buffer.from('<html><script>') });
  await attachments.getByRole('button', { name: 'Attach file' }).click();
  await expect(attachments.getByTestId('form-error')).toContainText('Only PDF, JPEG and PNG');

  // The receipt shows the order, the new price and the attachment, which downloads as a file.
  await page.getByRole('link', { name: 'Goods receipts' }).click();
  await page.getByTestId('grns-table').getByRole('row', { name: new RegExp(`KT-${stamp}`, 'i') }).getByRole('button').click();
  const view = page.getByTestId('grn-view');
  await expect(view).toContainText('against order');
  await expect(view).toContainText('new price Rs. 58.00');
  const download = page.waitForEvent('download');
  await view.getByRole('link', { name: 'kaveri-invoice.pdf' }).click();
  expect((await download).suggestedFilename()).toBe('kaveri-invoice.pdf');

  // The order is complete and closed itself.
  await page.getByRole('link', { name: 'Purchase orders' }).click();
  // The page may still be settling after navigation (a re-render resets the filter), so retry until the row shows.
  const orderRow = page.getByTestId('orders-table').getByRole('row', { name: new RegExp(supplierName) });
  await expect(async () => {
    await page.getByLabel('Status').selectOption({ label: 'All' });
    await expect(orderRow).toBeVisible({ timeout: 2000 });
  }).toPass({ timeout: 20000 });
  await expect(orderRow).toContainText('Closed');
  await expect(orderRow).toContainText('Received');
});
