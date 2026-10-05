import { expect, test } from '@playwright/test';
import { apps, expectSignedIn, owner, signIn } from '../support/env';

// Month close screen (spec section 22): the months with their state, a month's checklist, and the archive key exchange.
// Locking is not exercised here: a locked month would refuse records other specs date in it (it is covered by the API
// tests on their own installation).
test('the owner sees which months can be closed, what stops them, and the keys for the archive server', async ({ page }) => {
  await signIn(page, apps.billing, owner.username, owner.password);
  await expectSignedIn(page, owner.name);
  await page.getByRole('link', { name: 'Month close' }).click();

  const months = page.getByTestId('months-table');
  const current = new Date().toLocaleDateString('en-CA', { timeZone: 'Asia/Kolkata' }).slice(0, 7);
  await expect(months.getByRole('row', { name: new RegExp(`^${current} In progress`) })).toBeVisible();

  // The keys: this server's signing key to copy, and a place for the archive's key (a wrong one is refused).
  const keys = page.getByTestId('archive-keys');
  await expect(keys.getByLabel("This server's public key")).toHaveValue(/-----BEGIN PUBLIC KEY-----/);
  await expect(keys).toContainText('No archive server is registered yet.');
  const form = keys.getByTestId('archive-recipient-form');
  await form.getByRole('textbox').fill('not a key');
  await form.getByRole('button', { name: 'Register the archive key' }).click();
  await expect(form.getByTestId('form-error')).toContainText("Paste the archive server's public key");

  // A month that is over shows its checklist; the earliest one has nothing before it to lock first.
  const over = months.getByRole('row').filter({ hasText: 'Over, not locked' }).last();
  await over.getByRole('button', { name: 'Open' }).click();
  const checks = page.getByTestId('month-checks');
  await expect(checks).toContainText('The month is over');
  await expect(checks).toContainText('Earlier months are locked');
  await expect(checks).toContainText('Stock balances equal the stock ledger');
});
