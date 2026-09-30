import { expect, test, type Page } from '@playwright/test';
import { apps, expectSignedIn, owner, signIn, signOut } from '../support/env';
import { nextTotpWindow, totp } from '../support/totp';

// One shared story, run in order: the owner sets up staff, a cashier signs in for the first time, turns on
// two-step verification, and a privileged role goes through maker-checker approval.
test.describe.configure({ mode: 'serial' });

const suffix = Date.now().toString(36);
const cashier = { username: `cashier.${suffix}`, name: 'Asha Cashier', temp: 'Temporary-Pass-001', password: 'Asha-Own-Password-9' };
const manager = { username: `manager.${suffix}`, name: 'Mani Manager', temp: 'Temporary-Pass-002', password: 'Mani-Own-Password-9' };
const auditor = { username: `auditor.${suffix}`, name: 'Kavi Auditor', temp: 'Temporary-Pass-003' };

async function addUser(page: Page, user: { username: string; name: string; temp: string }, role: string) {
  await page.goto(`${apps.billing}/admin/users`);
  await page.getByText('Add a user').click();
  const form = page.getByTestId('create-user-form');
  await form.getByLabel('Username').fill(user.username);
  await form.getByLabel('Full name').fill(user.name);
  await form.getByLabel('Temporary password').fill(user.temp);
  await form.getByLabel('Role').selectOption(role);
  await form.getByRole('button', { name: 'Add user' }).click();
  await expect(page.getByTestId(`user-row-${user.username}`)).toBeVisible();
}

async function firstSignIn(page: Page, user: { username: string; name: string; temp: string; password: string }) {
  await signIn(page, apps.billing, user.username, user.temp);
  const change = page.getByTestId('change-password-form');
  await expect(change).toBeVisible();
  await change.getByLabel('Current password').fill(user.temp);
  await change.getByLabel('New password', { exact: true }).fill(user.password);
  await change.getByLabel('Repeat new password').fill(user.password);
  await change.getByRole('button', { name: 'Change password' }).click();
  await expectSignedIn(page, user.name);
}

test('owner adds a store and staff; a cashier must change the temporary password and sees no admin areas', async ({ page }) => {
  await signIn(page, apps.billing, owner.username, owner.password);
  await expectSignedIn(page, owner.name);

  await page.getByRole('link', { name: 'Stores' }).click();
  await page.getByText('Add a store').click();
  const storeForm = page.getByTestId('create-store-form');
  await storeForm.getByLabel('Store code').fill('S02');
  await storeForm.getByLabel('Store name').fill('Second Street Store');
  await storeForm.getByLabel('GST state code').fill('33');
  await storeForm.getByRole('button', { name: 'Add store' }).click();
  await expect(page.getByTestId('stores-table')).toContainText('Second Street Store');

  await addUser(page, cashier, 'cashier');
  await addUser(page, manager, 'manager');
  await expect(page.getByTestId(`user-row-${manager.username}`)).toContainText('Manager');
  await signOut(page);

  await firstSignIn(page, cashier);
  await expect(page.getByRole('link', { name: 'Users' })).toHaveCount(0);
  await page.goto(`${apps.billing}/admin/users`);
  await expect(page.getByTestId('no-permission')).toBeVisible();
});

test('cashier turns on two-step verification and must then enter a code to sign in', async ({ page }) => {
  await signIn(page, apps.billing, cashier.username, cashier.password);
  await expectSignedIn(page, cashier.name);
  await page.getByRole('link', { name: 'My account' }).click();
  await page.getByTestId('mfa-start').getByRole('button', { name: 'Start setup' }).click();
  const secret = (await page.getByTestId('mfa-secret').textContent())!.trim();
  await expect(page.getByRole('img', { name: 'QR code for your authenticator app' })).toBeVisible();

  const confirm = page.getByTestId('mfa-confirm-form');
  await confirm.getByLabel('Code').fill(totp(secret));
  await confirm.getByRole('button', { name: 'Turn on two-step verification' }).click();
  await expect(page.getByTestId('recovery-codes').locator('li')).toHaveCount(10);
  await page.getByRole('button', { name: 'I have saved them - continue' }).click();
  await signOut(page);

  await signIn(page, apps.billing, cashier.username, cashier.password);
  const verify = page.getByTestId('mfa-verify-form');
  await expect(verify).toBeVisible();
  await verify.getByLabel('Code').fill('000000');
  await verify.getByRole('button', { name: 'Verify' }).click();
  await expect(page.getByTestId('form-error')).toBeVisible();

  await nextTotpWindow(); // the enrolment code's time step cannot be reused
  await verify.getByLabel('Code').fill(totp(secret));
  await verify.getByRole('button', { name: 'Verify' }).click();
  await expectSignedIn(page, cashier.name);
});

test('a privileged role waits for a second person, who approves it in the Owner Dashboard', async ({ page }) => {
  await firstSignIn(page, manager);
  await signOut(page);

  // The owner asks for an Auditor: privileged, so it goes to the approval queue.
  await signIn(page, apps.billing, owner.username, owner.password);
  await addUser(page, auditor, 'auditor');
  await expect(page.getByRole('status').filter({ hasText: 'waiting for another authorised person' })).toBeVisible();
  await expect(page.getByTestId(`user-row-${auditor.username}`).getByTestId('pending-role')).toHaveText('Auditor - awaiting approval');

  // The owner cannot approve their own request.
  await page.getByRole('link', { name: 'Approvals' }).click();
  const ownView = page.getByTestId('approval-item').filter({ hasText: auditor.username });
  await expect(ownView).toBeVisible();
  await expect(ownView.getByRole('button', { name: 'Approve' })).toHaveCount(0);
  await signOut(page);

  // The manager approves it from the Owner Dashboard.
  await signIn(page, apps.owner, manager.username, manager.password);
  await expectSignedIn(page, manager.name);
  await page.getByRole('link', { name: 'Approvals' }).click();
  const item = page.getByTestId('approval-item').filter({ hasText: auditor.username });
  page.once('dialog', (dialog) => void dialog.accept('Checked with the owner'));
  await item.getByRole('button', { name: 'Approve' }).click();
  await expect(page.getByTestId('approval-item').filter({ hasText: auditor.username })).toHaveCount(0);

  await page.getByRole('link', { name: 'Users' }).click();
  const approvedRow = page.getByTestId(`user-row-${auditor.username}`);
  await expect(approvedRow).toContainText('Auditor');
  await expect(approvedRow.getByTestId('pending-role')).toHaveCount(0);

  await page.getByRole('link', { name: 'Audit trail' }).click();
  await expect(page.getByTestId('audit-table')).toContainText('approval.approved');
  await expect(page.getByTestId('audit-table')).toContainText('role.granted');
});

test('a manager-issued reset code lets a user set a new password from the sign-in page', async ({ page }) => {
  await signIn(page, apps.billing, owner.username, owner.password);
  await page.goto(`${apps.billing}/admin/users`);
  await page.getByTestId(`reset-${manager.username}`).click();
  const notice = page.getByRole('status').filter({ hasText: 'Reset code for' });
  const code = (await notice.textContent())!.match(/[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}/)![0];
  await signOut(page);

  await page.getByRole('button', { name: 'I have a password reset code' }).click();
  await page.getByLabel('Username').fill(manager.username);
  await page.getByLabel('Reset code from your manager').fill(code);
  await page.getByLabel('New password').fill('Mani-Reset-Password-5');
  await page.getByRole('button', { name: 'Set new password' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Password changed' })).toBeVisible();
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();

  await signIn(page, apps.billing, manager.username, 'Mani-Reset-Password-5');
  await expectSignedIn(page, manager.name);
});
