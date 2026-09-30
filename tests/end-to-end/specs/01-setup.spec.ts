import { expect, test } from '@playwright/test';
import { apps, expectSignedIn, owner, readSetupCode, signIn } from '../support/env';

// Runs first, against the freshly created e2e database: the installation starts empty.
test('first-run setup creates the business, store and owner, who can then sign in', async ({ page }) => {
  await page.goto(apps.billing);
  const form = page.getByTestId('setup-form');
  await expect(form).toBeVisible();

  // A wrong setup code is refused and nothing is created.
  await form.getByLabel('Setup code').fill('AAAA-BBBB-CCCC-DDDD');
  await form.getByLabel('Business code').fill('E2E');
  await form.getByLabel('Legal name').fill('E2E Traders Private Limited');
  await form.getByLabel('GST state code').fill('33');
  await form.getByLabel('Store name').fill('E2E Main Store');
  await form.getByLabel('Username').fill(owner.username);
  await form.getByLabel('Your name').fill(owner.name);
  await form.getByLabel('Password', { exact: true }).fill(owner.password);
  await form.getByLabel('Repeat password').fill(owner.password);
  await form.getByRole('button', { name: 'Complete setup' }).click();
  await expect(page.getByTestId('form-error')).toContainText('setup code is not correct');

  await form.getByLabel('Setup code').fill(readSetupCode());
  await form.getByRole('button', { name: 'Complete setup' }).click();

  await expect(page.getByTestId('login-form')).toBeVisible();
  await signIn(page, apps.billing, owner.username, owner.password);
  await expectSignedIn(page, owner.name);
  await expect(page.getByRole('link', { name: 'Users' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Approvals' })).toBeVisible();
});
