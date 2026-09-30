import { expect, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

export const apps = {
  billing: 'http://localhost:3100',
  owner: 'http://localhost:3101',
  collection: 'http://localhost:3102',
  archive: 'http://localhost:3103',
} as const;

export const owner = { username: 'e2e.owner', password: 'E2E-Owner-Password-1', name: 'E2E Owner' };

export function readSetupCode(): string {
  return readFileSync(path.resolve(__dirname, '..', '.auth', 'setup-code.txt'), 'utf8').trim();
}

export async function signIn(page: Page, baseUrl: string, username: string, password: string) {
  await page.goto(baseUrl);
  const form = page.getByTestId('login-form');
  await form.getByLabel('Username').fill(username);
  await form.getByLabel('Password').fill(password);
  await form.getByRole('button', { name: 'Sign in' }).click();
  // Wait for the answer before doing anything else, so a following navigation cannot cancel the sign-in.
  await expect(form).toBeHidden();
}

export async function signOut(page: Page) {
  await page.getByRole('banner').getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByTestId('login-form')).toBeVisible();
}

export async function expectSignedIn(page: Page, displayName: string) {
  await expect(page.getByTestId('signed-in-user')).toHaveText(displayName);
}
