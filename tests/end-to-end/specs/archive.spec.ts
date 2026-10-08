import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { apps, expectSignedIn, owner, signIn } from '../support/env';

// The Owner Archive on its own archive server (D-042): first setup, its key for the store servers, trusting a store
// server's key, an archive user limited to some reports, and a file that is not a package being refused. Importing real
// months is covered by the API tests (a month of the shared test store cannot be locked without disturbing other tests),
// and so is registering the archive's key on the store (months.spec checks the store screen before any key is there).
const archiveSetupCode = () => readFileSync(path.resolve(__dirname, '..', '.auth', 'archive-setup-code.txt'), 'utf8').trim();

test('the owner sets up the archive, trusts the store server and adds a report user limited to some reports', async ({ browser }) => {
  // Separate browsers: here both servers are on localhost and would share cookies; in a shop they are different machines.
  const archiveContext = await browser.newContext();
  const storeContext = await browser.newContext();
  const archive = await archiveContext.newPage();
  const store = await storeContext.newPage();

  // First setup with the code from the archive server, then sign in as its owner administrator.
  await archive.goto(apps.archive);
  const setup = archive.getByTestId('archive-setup-form');
  await expect(setup).toBeVisible({ timeout: 30_000 });
  await setup.getByLabel('Setup code').fill(archiveSetupCode());
  await setup.getByLabel('Company code').fill('ARCH01');
  await setup.getByLabel('Company name').fill('E2E Traders Archive');
  await setup.getByLabel("Owner administrator's username").fill('archive.owner');
  await setup.getByLabel('Your name').fill('Archive Owner');
  await setup.getByLabel('Password', { exact: true }).fill('Archive-Owner-Pass-1');
  await setup.getByLabel('Repeat password').fill('Archive-Owner-Pass-1');
  await setup.getByRole('button', { name: 'Complete setup' }).click();
  await expect(archive.getByTestId('login-form')).toBeVisible();
  await signIn(archive, apps.archive, 'archive.owner', 'Archive-Owner-Pass-1');
  await expectSignedIn(archive, 'Archive Owner');
  await expect(archive.getByTestId('archive-upload')).toBeVisible();
  await expect(archive.getByText('No month has been archived yet.')).toBeVisible();

  // The archive shows its key for the store servers; the store server's signing key is trusted here.
  await archive.getByRole('link', { name: 'Store servers' }).click();
  await expect(archive.getByLabel("This archive's public key")).toHaveValue(/-----BEGIN PUBLIC KEY-----/);
  await expect(archive.getByText('No store server is trusted yet.')).toBeVisible();

  await signIn(store, apps.billing, owner.username, owner.password);
  await expectSignedIn(store, owner.name);
  await store.getByRole('link', { name: 'Month close' }).click();
  const storeKey = await store.getByTestId('archive-keys').getByLabel("This server's public key").inputValue();
  expect(storeKey).toContain('-----BEGIN PUBLIC KEY-----');

  const register = archive.getByTestId('register-source-form');
  await register.getByLabel('Name').fill('E2E store server');
  await register.getByRole('textbox', { name: /store server's public key/ }).fill(storeKey);
  await register.getByRole('button', { name: 'Trust this store server' }).click();
  await expect(archive.getByTestId('archive-sources')).toContainText('E2E store server');
  await expect(archive.getByTestId('archive-sources')).toContainText('0 month(s) imported');

  // A report user limited to two reports of the 2026-27 financial year.
  await archive.getByRole('link', { name: 'Users' }).click();
  const users = archive.getByTestId('archive-users');
  await expect(users).toContainText('Owner administrator: all businesses, all years');
  await users.getByText('Add a user').click();
  const form = archive.getByTestId('archive-user-form');
  await form.getByLabel('Username').fill('report.reader');
  await form.getByLabel('Name', { exact: true }).fill('Report Reader');
  await form.getByLabel('Temporary password').fill('Temporary-Pass-001');
  await form.getByLabel('Role').selectOption({ label: 'Report user' });
  await form.getByLabel(/^Financial year/).fill('2026');
  await form.getByLabel(/^Reports/).fill('sales-summary, gst-rates');
  await form.getByRole('button', { name: 'Add user' }).click();
  await expect(users).toContainText('User added.');
  // Report keys are kept sorted.
  await expect(users).toContainText('Report user: all businesses, FY 2026-27, reports: gst-rates, sales-summary');

  // Historical reports: nothing to report before a month is imported.
  await archive.getByRole('link', { name: 'Reports' }).click();
  await expect(archive.getByRole('heading', { name: 'Historical reports' })).toBeVisible();
  await expect(archive.getByText('No month has been archived yet.')).toBeVisible();

  // Something that is not a package is refused, with why.
  await archive.getByRole('link', { name: 'Archived months' }).click();
  const upload = archive.getByTestId('archive-upload');
  await upload.getByLabel('Package').setInputFiles({ name: 'notes.sbarc', mimeType: 'application/octet-stream', buffer: Buffer.from('not a package') });
  await upload.getByRole('button', { name: 'Import' }).click();
  await expect(upload.getByTestId('form-error')).toBeVisible();
  await expect(archive.getByText('No month has been archived yet.')).toBeVisible();

  // The store server has no archive of its own.
  expect((await store.request.get(`${apps.billing}/api/v1/archive/imports`)).status()).toBe(404);

  await archiveContext.close();
  await storeContext.close();
});
