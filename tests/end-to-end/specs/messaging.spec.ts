import { expect, test } from '@playwright/test';
import { api, apps, expectSignedIn, owner, signIn } from '../support/env';

// The owner switches WhatsApp on and names the approved receipt template; a payment from a debtor who agreed to
// WhatsApp is then confirmed to them (by the simulator in this environment) and shows on their account with its history.
test.describe.configure({ mode: 'serial' });

interface Me {
  memberships: { businessId: string }[];
}

test('a receipt is sent to the debtor on whatsapp and its delivery shows on their account', async ({ page }) => {
  const stamp = Date.now().toString(36).toUpperCase();
  const debtorCode = `WA${stamp}`.slice(0, 10);
  const debtorName = `Meena Stores ${stamp}`;

  await signIn(page, apps.billing, owner.username, owner.password);
  await expectSignedIn(page, owner.name);

  // Settings and wording.
  await page.getByRole('link', { name: 'WhatsApp & SMS' }).click();
  await expect(page.getByText('WhatsApp provider: Simulated')).toBeVisible();
  const settings = page.getByTestId('messaging-settings-form');
  await settings.getByLabel('Send WhatsApp messages').check();
  await settings.getByLabel('For receipts').check();
  await settings.getByRole('button', { name: 'Save messaging settings' }).click();
  await expect(settings.getByLabel('Send WhatsApp messages')).toBeChecked();

  await page.getByRole('button', { name: 'Edit Receipt WhatsApp' }).click();
  const template = page.getByTestId('template-form');
  await template.getByLabel('Approved template name').fill('payment_received');
  await template.getByRole('button', { name: 'Save wording' }).click();
  await expect(page.getByTestId('templates-table').getByRole('row', { name: /Receipt WhatsApp/ })).toContainText('payment_received');

  // A misspelt placeholder is refused, with the ones that can be used.
  await page.getByRole('button', { name: 'Edit Receipt SMS' }).click();
  await template.getByLabel('Text').fill('Received Rs. {{amont}}');
  await template.getByRole('button', { name: 'Save wording' }).click();
  await expect(template.getByTestId('form-error')).toContainText('Unknown placeholders: amont');

  // A debtor who agreed to WhatsApp pays Rs. 300 of Rs. 800.
  const business = (await api<Me>(page, 'GET', '/api/v1/auth/me')).memberships[0]!.businessId;
  await api(page, 'POST', `/api/v1/businesses/${business}/debtors`, {
    code: debtorCode,
    legalName: debtorName,
    stateCode: '33',
    whatsAppNumber: '9876501234',
    whatsAppConsent: true,
    creditPeriodDays: 15,
    creditLimit: 5000,
    openingBalance: 800,
    openingBalanceDate: '2026-09-01',
  });
  await page.getByRole('link', { name: 'Debtors' }).click();
  await page.getByTestId('debtors-table').getByRole('button', { name: debtorName }).click();
  const account = page.getByTestId('account-view');
  await account.getByText('Record a payment received').click();
  const receipt = account.getByTestId('debtor-receipt');
  await receipt.getByRole('combobox', { name: 'Paid by', exact: true }).selectOption('CASH');
  await receipt.getByLabel('Amount (Rs.)').fill('300');
  await receipt.getByRole('button', { name: 'Record receipt' }).click();
  await expect(receipt.getByRole('status')).toContainText(/Receipt .*\/RCT\/\d{6}: Rs\. 300\.00/);

  // The sender runs every few seconds; the account's message list shows it sent, with what was said.
  await expect(async () => {
    await page.reload();
    await page.getByTestId('debtors-table').getByRole('button', { name: debtorName }).click();
    const row = page.getByTestId('messages-table').getByRole('row', { name: /Receipt .*RCT/ });
    await expect(row).toContainText('WhatsApp');
    await expect(row).toContainText('+919876501234');
    await expect(row).toContainText('Sent', { timeout: 1_000 });
  }).toPass({ timeout: 45_000 });

  const messages = page.getByTestId('messages-table');
  await messages.getByRole('button', { name: 'History' }).first().click();
  await expect(messages).toContainText(`Dear ${debtorName}, we received Rs. 300.00 by cash`);
  await expect(messages).toContainText('Previous balance Rs. 800.00, now Rs. 500.00.');
  await expect(messages).toContainText('Waiting to send');
  await expect(messages).toContainText(/Sent - Simulated: sim\.whatsapp\./);
});
