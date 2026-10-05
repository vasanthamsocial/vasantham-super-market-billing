import { expect, test } from '@playwright/test';
import { api, apps, expectSignedIn, owner, signIn } from '../support/env';

test('Collection App signs in on a phone without horizontal scrolling', async ({ page }) => {
  await page.goto(apps.collection);
  await expect(page.getByTestId('login-form')).toBeVisible();
  await expect(page.getByTestId('api-status')).toHaveText('Connected', { timeout: 30_000 });

  await signIn(page, apps.collection, owner.username, owner.password);
  await expectSignedIn(page, owner.name);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(/^Today - \d{4}-\d{2}-\d{2}$/);
  await expect(page.getByTestId('day-summary')).toBeVisible();

  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(0);

  // The session cookie is HttpOnly: page scripts cannot read it.
  const visibleCookies = await page.evaluate(() => document.cookie);
  expect(visibleCookies).not.toContain('sb_session');
  expect(visibleCookies).toContain('sb_csrf');
});

interface Me {
  userId: string;
  memberships: { businessId: string }[];
}

/** Every record the app keeps in IndexedDB, as text: none may show what was collected. */
async function storedRecords(page: import('@playwright/test').Page): Promise<{ keys: string[]; texts: string[] }> {
  return page.evaluate(async () => {
    const db = await new Promise<IDBDatabase>((resolve, reject) => {
      const open = indexedDB.open('sb-offline');
      open.onsuccess = () => resolve(open.result);
      open.onerror = () => reject(open.error);
    });
    const read = <T,>(query: (s: IDBObjectStore) => IDBRequest<T>) =>
      new Promise<T>((resolve, reject) => {
        const request = query(db.transaction('records').objectStore('records'));
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
      });
    const keys = (await read((s) => s.getAllKeys())) as string[];
    const values = (await read((s) => s.getAll())) as { iv: Uint8Array; data: ArrayBuffer }[];
    db.close();
    return { keys, texts: values.map((v) => JSON.stringify(v) + new TextDecoder().decode(v.data)) };
  });
}

test('without signal, an enrolled phone keeps collections encrypted with a provisional receipt and posts them on reconnecting', async ({ page, context }) => {
  const stamp = Date.now().toString(36).toUpperCase();
  const debtorCode = `OF${stamp}`.slice(0, 10);
  const debtorName = `Kaveri Stores ${stamp}`;
  await signIn(page, apps.collection, owner.username, owner.password);
  await expectSignedIn(page, owner.name);

  // Set-up through the API: a party owing Rs. 900 assigned to the owner (who also collects) today, and an open round.
  const me = await api<Me>(page, 'GET', '/api/v1/auth/me');
  const business = `/api/v1/businesses/${me.memberships[0]!.businessId}`;
  const debtor = await api<{ id: string }>(page, 'POST', `${business}/debtors`, {
    code: debtorCode, legalName: debtorName, tradeName: null, gstin: null, stateCode: '33', address: 'Mill Road', phone: '9845011111',
    creditPeriodDays: 15, creditLimit: 5000, openingBalance: 900, openingBalanceDate: '2026-09-01',
  });
  const today = new Date().toLocaleDateString('en-CA', { timeZone: 'Asia/Kolkata' });
  await api(page, 'POST', `${business}/collection-visits`, { debtorId: debtor.id, collectorUserId: me.userId, visitDate: today, note: null });
  const round = await api<{ status: string } | undefined>(page, 'GET', `${business}/collections/session`);
  if (round?.status !== 'OPEN') {
    const stores = await api<{ id: string }[]>(page, 'GET', `${business}/stores`);
    await api(page, 'POST', `${business}/collections/sessions`, { storeId: stores[0]!.id });
  }

  // The manager (the owner) enrols this phone for the owner, Rs. 1000 within 24 hours; the device cookie is HttpOnly.
  await page.reload();
  const enrol = page.getByTestId('enrol-phone');
  await enrol.locator('summary').click();
  await enrol.getByLabel('Collector').selectOption({ label: owner.name });
  await enrol.getByLabel('Phone name').fill(`Phone ${stamp}`);
  await enrol.getByLabel('Most it may hold (Rs.)').fill('1000');
  await enrol.getByRole('button', { name: 'Enrol this phone' }).click();
  await expect(enrol).toContainText(`now collects without signal for ${owner.name}`);
  expect(await page.evaluate(() => document.cookie)).not.toContain('sb_collection_device');

  // Signed in on the enrolled phone: the collector and the day are kept for working offline.
  await page.reload();
  const party = page.getByTestId(`day-party-${debtorCode}`);
  await expect(party).toBeVisible();
  await expect(page.getByTestId('pending-summary')).toHaveText('Nothing waiting to be sent.');
  await expect.poll(async () => (await storedRecords(page)).keys).toEqual(expect.arrayContaining(['device', 'me', 'day']));

  // No signal: the collection stays on the phone with a provisional receipt and no balance.
  await context.setOffline(true);
  await party.getByRole('button', { name: 'Collect' }).click();
  await party.getByTestId('collect-form').getByLabel('Amount (Rs.)').fill('300');
  await party.getByTestId('collect-form').getByRole('button', { name: 'Record collection' }).click();
  const provisional = page.getByTestId('provisional-receipt');
  await expect(provisional).toContainText(/Provisional receipt P-1: Rs\. 300\.00 \(Cash\) from Kaveri Stores/);
  await expect(provisional).toContainText('the party\'s balance is confirmed then');
  await expect(provisional).not.toContainText('owes');
  await expect(page.getByTestId('pending-summary')).toHaveText('1 collection waiting to be sent: Rs. 300.00.');

  // Everything kept on the phone is encrypted: neither the party nor the amount can be read from storage.
  const stored = await storedRecords(page);
  expect(stored.keys).toContain('queue:000000000001');
  for (const text of stored.texts) {
    expect(text).not.toContain(debtorName);
    expect(text).not.toContain(debtorCode);
    expect(text).not.toContain(debtor.id);
    expect(text).not.toContain(owner.username);
  }

  // The app opens with the store server unreachable, for the remembered collector, with the day as last loaded.
  await context.setOffline(false);
  await page.route('**/api/**', (route) => route.abort('internetdisconnected'));
  await page.reload();
  await expect(page.getByTestId('offline-banner')).toContainText('Collections are kept on this phone');
  await expectSignedIn(page, owner.name);
  await expect(party).toBeVisible();
  await expect(page.getByTestId('pending-summary')).toHaveText('1 collection waiting to be sent: Rs. 300.00.');

  // Beyond the phone's limit (Rs. 300 held of Rs. 1000) it is refused; within it, a second one is kept.
  await party.getByRole('button', { name: 'Collect' }).click();
  await party.getByTestId('collect-form').getByLabel('Amount (Rs.)').fill('800');
  await party.getByTestId('collect-form').getByRole('button', { name: 'Record collection' }).click();
  await expect(party.getByTestId('collect-form')).toContainText('may hold at most Rs. 1000.00');
  await party.getByTestId('collect-form').getByLabel('Amount (Rs.)').fill('200');
  await party.getByTestId('collect-form').getByRole('button', { name: 'Record collection' }).click();
  await expect(provisional).toContainText('Provisional receipt P-2: Rs. 200.00');
  await expect(page.getByTestId('pending-summary')).toHaveText('2 collections waiting to be sent: Rs. 500.00.');

  // The signal returns: both are posted in order, and the party owes Rs. 400.
  await context.setOffline(true);
  await page.unroute('**/api/**');
  await context.setOffline(false);
  await expect(page.getByTestId('pending-summary')).toHaveText('Nothing waiting to be sent.', { timeout: 45_000 });
  const synced = page.getByTestId('synced-collections');
  await expect(synced).toContainText(/P-1: Kaveri Stores \S+, Rs\. 300\.00 - Posted, receipt \S+\/RCT\/\d{6}/);
  await expect(synced).toContainText(/P-2: Kaveri Stores \S+, Rs\. 200\.00 - Posted, receipt \S+\/RCT\/\d{6}, now owes Rs\. 400\.00/);
  await expect(page.getByTestId('offline-banner')).toBeHidden();
  await expect(party).toContainText('Collected Rs. 500.00');
});
