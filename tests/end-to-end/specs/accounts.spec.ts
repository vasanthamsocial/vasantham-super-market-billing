import { expect, test } from '@playwright/test';
import { apps, expectSignedIn, owner, signIn } from '../support/env';

// The owner starts supplier and debtor accounts with their opening balances, pays part of what a supplier is owed,
// and asks for a correction to a debtor's account, which needs another person to approve and so is refused here.
test.describe.configure({ mode: 'serial' });

test('owner enters opening balances, pays a supplier and asks for a correction', async ({ page }) => {
  const stamp = Date.now().toString(36).toUpperCase();
  const supplierCode = `AS${stamp}`.slice(0, 10);
  const supplierName = `Annapoorna Mills ${stamp}`;
  const debtorCode = `AD${stamp}`.slice(0, 10);
  const debtorName = `Murugan Provisions ${stamp}`;

  await signIn(page, apps.billing, owner.username, owner.password);
  await expectSignedIn(page, owner.name);

  // A supplier owed Rs. 500 from before, on 30 days' credit.
  await page.getByRole('link', { name: 'Suppliers' }).click();
  const supplierForm = page.getByTestId('add-supplier-form');
  await supplierForm.getByLabel('Code', { exact: true }).fill(supplierCode);
  await supplierForm.getByLabel('Legal name', { exact: true }).fill(supplierName);
  await supplierForm.getByLabel('GST state code').fill('33');
  await supplierForm.getByLabel('Credit period (days)').fill('30');
  await supplierForm.getByLabel('Opening balance (Rs., optional)').fill('500');
  await supplierForm.getByLabel('Opening balance as of').fill('2026-09-01');
  await supplierForm.getByRole('button', { name: 'Add supplier' }).click();
  const supplierRow = page.getByTestId('suppliers-table').getByRole('row', { name: new RegExp(supplierCode) });
  await expect(supplierRow).toContainText('500.00');
  await expect(supplierRow).toContainText('30 days');

  // Pay Rs. 200 by bank transfer: it goes against the opening balance.
  await supplierRow.getByRole('button', { name: supplierName }).click();
  const account = page.getByTestId('account-view');
  await expect(account.getByTestId('account-balance')).toContainText('Owed to supplier: Rs. 500.00');
  await account.getByText('Pay this supplier').click();
  const payment = account.getByTestId('supplier-payment');
  await payment.getByLabel('Amount (Rs.)').fill('200');
  await payment.getByLabel('Reference (optional)').fill('UTR-778899');
  await payment.getByRole('button', { name: 'Record payment' }).click();
  await expect(payment.getByRole('status')).toContainText(/Recorded .*\/PMT\/\d{6}: Rs\. 200\.00, paying opening balance/);
  await expect(account.getByTestId('account-balance')).toContainText('Owed to supplier: Rs. 300.00');
  await expect(account.getByTestId('open-charges')).toContainText('300.00');
  await expect(account.getByTestId('statement')).toContainText('UTR-778899');

  // A debtor on Rs. 5,000 credit who owes Rs. 1,000 from before, and agreed to WhatsApp messages.
  await page.getByRole('link', { name: 'Debtors' }).click();
  const debtorForm = page.getByTestId('add-debtor-form');
  await debtorForm.getByLabel('Code', { exact: true }).fill(debtorCode);
  await debtorForm.getByLabel('Legal name', { exact: true }).fill(debtorName);
  await debtorForm.getByLabel('WhatsApp number').fill('98400 12345');
  await debtorForm.getByLabel('Agreed to receive WhatsApp messages').check();
  await debtorForm.getByLabel('Credit period (days)').fill('15');
  await debtorForm.getByLabel('Credit limit (Rs.)').fill('5000');
  await debtorForm.getByLabel('Opening balance (Rs., optional)').fill('1000');
  await debtorForm.getByLabel('Opening balance as of').fill('2026-09-01');
  await debtorForm.getByRole('button', { name: 'Add debtor' }).click();
  const debtorRow = page.getByTestId('debtors-table').getByRole('row', { name: new RegExp(debtorCode) });
  await expect(debtorRow).toContainText('+919840012345');
  await expect(debtorRow).toContainText('1,000.00');

  // A correction needs someone else's approval.
  await debtorRow.getByRole('button', { name: debtorName }).click();
  const debtorAccount = page.getByTestId('account-view');
  await expect(debtorAccount.getByTestId('statement')).toContainText('Opening balance');
  await debtorAccount.getByText('Ask for a correction').click();
  const correction = debtorAccount.getByTestId('adjustment-form');
  await correction.getByLabel('Correction (Rs.)').fill('-200');
  await correction.getByLabel('Reason').fill('Discount agreed at year end');
  await correction.getByRole('button', { name: 'Ask for approval' }).click();
  // This business has nobody else who can approve, and money corrections are never waived.
  await expect(correction.getByTestId('form-error')).toContainText('Nobody else in the business can approve account corrections');
  await expect(debtorAccount.getByTestId('account-balance')).toContainText('Owed by debtor: Rs. 1,000.00');
});
