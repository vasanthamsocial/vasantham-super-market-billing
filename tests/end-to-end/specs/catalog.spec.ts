import { expect, test } from '@playwright/test';
import { apps, expectSignedIn, owner, signIn } from '../support/env';

// The owner builds a product with a barcode, MRP and prices, and checks which price a bill would use.
test.describe.configure({ mode: 'serial' });

function ean13(first12: string): string {
  const sum = [...first12].reduce((total, digit, index) => total + Number(digit) * (index % 2 === 0 ? 1 : 3), 0);
  return first12 + ((10 - (sum % 10)) % 10);
}

test('owner adds a product, prices it, and the price checker applies member and MRP rules', async ({ page }) => {
  const code = `ATTA${Date.now().toString(36).toUpperCase()}`.slice(0, 12);
  const barcode = ean13(`890${Date.now()}`.slice(0, 12));

  await signIn(page, apps.billing, owner.username, owner.password);
  await expectSignedIn(page, owner.name);
  await page.getByRole('link', { name: 'Products' }).click();

  await page.getByText('Add a product').click();
  const form = page.getByTestId('create-product-form');
  await expect(form.getByLabel('Stock unit')).toContainText('PCS'); // units are loaded
  await form.getByLabel('Product code (SKU)').fill(code);
  await form.getByLabel('Product name', { exact: true }).fill('Whole Wheat Atta 5 kg');
  await form.getByLabel('HSN/SAC').fill('1101');
  await form.getByLabel('GST rate %').fill('5');
  await form.getByLabel('Barcode').fill(barcode);
  await form.getByLabel('MRP (Rs.)').fill('280');
  await form.getByRole('button', { name: 'Add product' }).click();
  await page.getByRole('link', { name: 'Whole Wheat Atta 5 kg' }).first().click();
  await expect(page.getByTestId('product-name')).toHaveText('Whole Wheat Atta 5 kg');
  await expect(page.getByTestId('packs-table')).toContainText(barcode);
  await expect(page.getByTestId('packs-table')).toContainText('Rs. 280.00');

  // Standard and member prices.
  await page.getByText('Add a price').click();
  const addPrice = async (type: string, price: string) => {
    const priceForm = page.getByTestId('add-price-form');
    await priceForm.getByLabel('Price type').selectOption(type);
    await priceForm.getByLabel('Price (Rs.)').fill(price);
    await priceForm.getByRole('button', { name: 'Add price' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Price is active' })).toBeVisible();
  };
  await addPrice('STANDARD', '265');
  await addPrice('MEMBER', '255');
  await expect(page.getByTestId('prices-table')).toContainText('Rs. 265.00');

  // Above MRP is refused with a clear message.
  const priceForm = page.getByTestId('add-price-form');
  await priceForm.getByLabel('Price type').selectOption('STANDARD');
  await priceForm.getByLabel('Price (Rs.)').fill('290');
  await priceForm.getByRole('button', { name: 'Add price' }).click();
  await expect(priceForm.getByTestId('form-error')).toContainText('above the MRP');

  // Price checker: a member pays the member price.
  await page.getByText('Check the price a bill would use').click();
  const checker = page.getByTestId('price-check-form');
  await checker.getByRole('button', { name: 'Check price' }).click();
  await expect(page.getByTestId('price-quote')).toContainText('Rs. 265.00');
  await checker.getByLabel('Customer is a member').check();
  await checker.getByRole('button', { name: 'Check price' }).click();
  await expect(page.getByTestId('price-quote')).toContainText('Rs. 255.00');
});

test('GST registration shows the mode chosen at setup', async ({ page }) => {
  await signIn(page, apps.billing, owner.username, owner.password);
  await page.getByRole('link', { name: 'GST registration' }).click();
  await expect(page.getByTestId('current-tax-mode')).toHaveText('Not GST registered');
  await expect(page.getByTestId('tax-history')).toContainText('Initial registration recorded at setup');
});
