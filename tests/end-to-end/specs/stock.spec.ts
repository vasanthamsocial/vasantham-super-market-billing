import { expect, test } from '@playwright/test';
import { apps, expectSignedIn, owner, signIn } from '../support/env';

// The owner enters opening stock, writes off damaged items, and checks stock on hand, its value and the item ledger.
test.describe.configure({ mode: 'serial' });

test('owner posts opening stock and damage, and stock on hand shows the FIFO value and every movement', async ({ page }) => {
  const code = `OIL${Date.now().toString(36).toUpperCase()}`.slice(0, 12);
  const name = `Sunflower Oil 1 L ${code}`;

  await signIn(page, apps.billing, owner.username, owner.password);
  await expectSignedIn(page, owner.name);

  // An item to stock.
  await page.getByRole('link', { name: 'Products' }).click();
  await page.getByText('Add a product').click();
  const product = page.getByTestId('create-product-form');
  await expect(product.getByLabel('Stock unit')).toContainText('PCS');
  await product.getByLabel('Product code (SKU)').fill(code);
  await product.getByLabel('Product name', { exact: true }).fill(name);
  await product.getByLabel('HSN/SAC').fill('1512');
  await product.getByRole('button', { name: 'Add product' }).click();
  await expect(page.getByRole('status').filter({ hasText: `${name} added` })).toBeVisible();

  // Opening stock: 24 at Rs. 40.
  await page.getByRole('link', { name: 'Stock documents' }).click();
  const form = page.getByTestId('stock-document-form');
  await form.getByLabel('Document type').selectOption('OPENING');
  const addItem = async () => {
    await form.getByLabel('Find an item to add').fill(code);
    await form.getByRole('button', { name: 'Find item' }).click();
    await form.getByRole('button', { name: `Add ${code} - ${name}` }).click();
    await expect(form.getByTestId('stock-lines')).toContainText(name);
  };
  await addItem();
  await form.getByLabel(`Quantity of ${name}`).fill('24');
  await form.getByLabel(`Cost of ${name}`).fill('40');
  await form.getByLabel('Reason').fill('Stock at go-live');
  await form.getByRole('button', { name: 'Post document' }).click();
  await expect(page.getByRole('status').filter({ hasText: /Posted .*\/OPN\/\d{6}/ })).toBeVisible();

  // Damage: 4 written off.
  await form.getByLabel('Document type').selectOption('DAMAGE');
  await addItem();
  await form.getByLabel(`Quantity of ${name}`).fill('4');
  await form.getByLabel('Reason').fill('Leaking bottles');
  await form.getByRole('button', { name: 'Post document' }).click();
  await expect(page.getByRole('status').filter({ hasText: /Posted .*\/DMG\/\d{6}/ })).toBeVisible();

  // Taking more than is in stock is refused with a clear message.
  await addItem();
  await form.getByLabel(`Quantity of ${name}`).fill('25');
  await form.getByLabel('Reason').fill('Too many');
  await form.getByRole('button', { name: 'Post document' }).click();
  await expect(form.getByTestId('form-error')).toContainText('not enough stock');

  // The damage document lists its movement at the opening cost.
  await page.getByTestId('stock-documents-table').getByRole('button', { name: /\/DMG\// }).first().click();
  await expect(page.getByTestId('stock-document-view')).toContainText('Leaking bottles');
  await expect(page.getByTestId('stock-document-view')).toContainText('-160.00');

  // Stock on hand: 20 left, worth Rs. 800.
  await page.getByRole('link', { name: 'Stock', exact: true }).click();
  await page.getByLabel('Search stock by item name or code').fill(code);
  await page.getByRole('button', { name: 'Search' }).click();
  const row = page.getByTestId('stock-table').getByRole('row', { name: new RegExp(code) });
  await expect(row).toContainText('20 PCS');
  await expect(row).toContainText('800.00');

  await row.getByRole('button', { name }).click();
  const ledger = page.getByTestId('stock-ledger');
  await expect(ledger).toContainText('Opening');
  await expect(ledger).toContainText('Damage');

  // The valuation method is fixed now that stock has moved.
  await page.getByRole('link', { name: 'Stock settings' }).click();
  await expect(page.getByTestId('valuation-method')).toContainText('FIFO');
  await expect(page.getByTestId('valuation-method')).toContainText('fixed now that stock has moved');
});
