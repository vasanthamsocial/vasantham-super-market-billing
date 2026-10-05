import { expect, test, type Browser, type Page } from '@playwright/test';
import { api, apps, ean13, expectSignedIn, owner, signIn } from '../support/env';

// The owner lists a lorry service with its booking office, a destination branch and the route between them, and sets
// it as a regular customer's usual way. At the counter the bill is filled in to go by that lorry. Its packing challan
// is picked by the owner, checked by a store hand on another PC, and packed; dispatch then books it with an LR, it shows
// in the LR/GR register with the freight and the expected delivery date, and its delivery is reported.
test.describe.configure({ mode: 'serial' });

interface Me {
  memberships: { businessId: string }[];
}

interface Product {
  variants: { id: string; units: { id: string }[] }[];
}

/** An item at Rs. 52 with stock, and a debtor in Madurai, through the API. */
async function setUp(page: Page, barcode: string, itemName: string, debtorCode: string, debtorName: string) {
  const business = (await api<Me>(page, 'GET', '/api/v1/auth/me')).memberships[0]!.businessId;
  const store = (await api<{ id: string }[]>(page, 'GET', `/api/v1/businesses/${business}/stores`))[0]!.id;
  const pcs = (await api<{ id: string; code: string }[]>(page, 'GET', `/api/v1/businesses/${business}/catalog/units`)).find((u) => u.code === 'PCS')!.id;
  const product = await api<Product>(page, 'POST', `/api/v1/businesses/${business}/catalog/products`, {
    code: `D${barcode.slice(-8)}`,
    name: itemName,
    printName: null,
    categoryId: null,
    brandId: null,
    baseUnitId: pcs,
    hsnSac: '1905',
    supplyType: 'TAXABLE',
    gstRatePercent: 5,
    cessRatePercent: 0,
    isWeighed: false,
    tracksBatches: false,
    tracksExpiry: false,
    tracksSerials: false,
    variant: { code: null, name: null, barcode, mrp: 55 },
  });
  const variant = product.variants[0]!;
  const pack = variant.units[0]!.id;
  await api(page, 'POST', `/api/v1/businesses/${business}/prices/variants/${variant.id}`, {
    variantUnitId: pack,
    rateType: 'STANDARD',
    channel: 'RETAIL',
    price: 52,
    taxInclusive: true,
    mrp: null,
    storeId: null,
    customerGroupId: null,
    membersOnly: false,
    minQuantity: 0,
    maxQuantity: null,
    validFromUtc: null,
    validToUtc: null,
    priority: null,
    note: null,
  });
  await api(page, 'POST', `/api/v1/businesses/${business}/stock/documents`, {
    type: 'OPENING',
    storeId: store,
    targetStoreId: null,
    reason: 'E2E stock',
    note: null,
    idempotencyKey: crypto.randomUUID(),
    negativeStockOverride: false,
    lines: [{ variantId: variant.id, variantUnitId: pack, quantity: 20, unitCost: 40 }],
  });
  await api(page, 'POST', `/api/v1/businesses/${business}/debtors`, {
    code: debtorCode,
    legalName: debtorName,
    stateCode: '33',
    address: '7 Town Hall Road, Madurai',
    phone: '9845067890',
    creditPeriodDays: 30,
    creditLimit: 5000,
  });
}

/** A store hand (inventory operator) checks the picked challan from their own PC (a separate browser). */
async function checkOnAnotherPc(browser: Browser, owner: Page, partyName: string, stamp: string) {
  const hand = { username: `packer.${stamp.toLowerCase()}`, name: `Packer ${stamp}`, temp: 'Temporary-Pass-011', password: 'Packer-Own-Password-9' };
  await owner.goto(`${apps.billing}/admin/users`);
  await owner.getByText('Add a user').click();
  const form = owner.getByTestId('create-user-form');
  await form.getByLabel('Username').fill(hand.username);
  await form.getByLabel('Full name').fill(hand.name);
  await form.getByLabel('Temporary password').fill(hand.temp);
  await form.getByLabel('Role').selectOption('inventory_operator');
  await form.getByRole('button', { name: 'Add user' }).click();
  await expect(owner.getByTestId(`user-row-${hand.username}`)).toBeVisible();

  const context = await browser.newContext();
  try {
    const page = await context.newPage();
    await signIn(page, apps.billing, hand.username, hand.temp);
    const change = page.getByTestId('change-password-form');
    await change.getByLabel('Current password').fill(hand.temp);
    await change.getByLabel('New password', { exact: true }).fill(hand.password);
    await change.getByLabel('Repeat new password').fill(hand.password);
    await change.getByRole('button', { name: 'Change password' }).click();
    await expectSignedIn(page, hand.name);
    await page.goto(`${apps.billing}/dispatch/packing`);
    await page.getByTestId('challans-table').getByRole('row', { name: new RegExp(partyName) }).getByRole('button').click();
    const card = page.getByTestId('challan-card');
    await expect(card.getByTestId('challan-progress')).toHaveText('To check');
    await card.getByTestId('check-form').getByRole('button', { name: 'Record check' }).click();
    await expect(card.getByTestId('challan-progress')).toHaveText('To pack');
  } finally {
    await context.close();
  }
  return hand.name;
}

function localDate(daysAhead: number): string {
  const date = new Date();
  date.setDate(date.getDate() + daysAhead);
  return date.toLocaleDateString('en-CA');
}

test('a bill goes by the customer\'s usual lorry service, is packed by two people and its LR shows in the register', async ({ page, browser }) => {
  const stamp = Date.now().toString(36).toUpperCase();
  const barcode = ean13(`892${Date.now()}`.slice(0, 12));
  const itemName = `Marie Biscuits ${barcode.slice(-4)}`;
  const debtorCode = `LS${stamp}`.slice(0, 10);
  const debtorName = `Madurai Traders ${stamp}`;
  const transporterName = `KPN Parcel ${stamp}`.slice(0, 30);
  const counterCode = `L${stamp}`.slice(0, 6);
  const lr = `LR${stamp}`.slice(0, 12);

  await signIn(page, apps.billing, owner.username, owner.password);
  await expectSignedIn(page, owner.name);
  await setUp(page, barcode, itemName, debtorCode, debtorName);

  // The lorry service: a booking office near the store, a branch in Madurai, two days between them.
  await page.getByRole('link', { name: 'Lorry services' }).click();
  const add = page.getByTestId('add-transporter-form');
  await add.getByLabel('Code', { exact: true }).fill(`KPN${stamp}`.slice(0, 12));
  await add.getByLabel('Name', { exact: true }).fill(transporterName);
  await add.getByLabel('Phone', { exact: true }).fill('0422 2345678');
  await add.getByRole('button', { name: 'Add lorry service' }).click();
  const card = page.getByTestId('transporter-card');
  await expect(card.getByRole('heading', { name: transporterName })).toBeVisible();
  const branch = card.getByTestId('add-branch-form');
  await branch.getByLabel('Branch name').fill('Gandhipuram office');
  await branch.getByLabel('City').fill('Coimbatore');
  await branch.getByLabel('We book goods here').check();
  await branch.getByLabel('Customers collect goods here').uncheck();
  await branch.getByRole('button', { name: 'Add branch' }).click();
  await expect(card.getByTestId('branches-table')).toContainText('Gandhipuram office');
  await branch.getByLabel('Branch name').fill('Madurai branch');
  await branch.getByLabel('City').fill('Madurai');
  await branch.getByLabel('We book goods here').uncheck();
  await branch.getByLabel('Customers collect goods here').check();
  await branch.getByRole('button', { name: 'Add branch' }).click();
  await expect(card.getByTestId('branches-table')).toContainText('Madurai branch');
  const route = card.getByTestId('add-route-form');
  await route.getByRole('combobox', { name: 'To (destination branch)', exact: true }).selectOption({ label: 'Madurai branch, Madurai' });
  await route.getByLabel('Transit days').fill('2');
  await route.getByRole('button', { name: 'Add route' }).click();
  await expect(card.getByTestId('transporter-routes-table')).toContainText('Gandhipuram office, Coimbatore');
  await expect(card.getByTestId('transporter-routes-table')).toContainText('Madurai branch, Madurai');

  // The customer usually gets their goods by this lorry, collected at Madurai.
  await page.getByRole('link', { name: 'Debtors' }).click();
  await page.getByTestId('debtors-table').getByRole('button', { name: debtorName }).click();
  const usual = page.getByTestId('delivery-preference');
  await usual.getByRole('combobox', { name: 'Delivery', exact: true }).selectOption('LORRY');
  await usual.getByRole('combobox', { name: 'Lorry service', exact: true }).selectOption({ label: transporterName });
  await usual.getByRole('combobox', { name: 'Destination branch', exact: true }).selectOption({ label: 'Madurai branch, Madurai' });
  await usual.getByRole('button', { name: 'Save usual delivery' }).click();
  await expect(usual).toContainText(`Lorry service: ${transporterName} to Madurai branch, Madurai`);

  // This browser becomes a counter with Rs. 500 in the drawer.
  await page.getByRole('link', { name: 'Counters' }).click();
  await page.getByText('Add a counter').click();
  const create = page.getByTestId('create-counter-form');
  await create.getByLabel('Counter code').fill(counterCode);
  await create.getByLabel('Counter name').fill('E2E dispatch counter');
  await create.getByRole('button', { name: 'Add counter' }).click();
  await page.getByTestId('enrol-device-form').getByRole('button', { name: 'Enrol this browser' }).click();
  await expect(page.getByTestId('devices-table')).toContainText('(this browser)');
  await page.getByRole('link', { name: 'Billing (POS)' }).click();
  const openShift = page.getByTestId('open-shift');
  await openShift.getByLabel('Number of Rs. 500', { exact: true }).fill('1');
  await openShift.getByRole('button', { name: 'Open shift with Rs. 500.00' }).click();

  // Bill to the customer: the payment screen fills in their usual lorry.
  const scan = page.getByLabel('Scan or type an item');
  await expect(scan).toBeFocused();
  await scan.press('F3');
  const picker = page.getByTestId('pos-account-picker');
  await picker.getByLabel('Find a customer account').fill(debtorCode);
  await picker.getByRole('button', { name: 'Find' }).click();
  await picker.getByRole('button', { name: new RegExp(debtorName) }).click();
  await expect(scan).toBeFocused();
  await scan.fill(`2*${barcode}`);
  await scan.press('Enter');
  await expect(page.getByTestId('pos-total')).toContainText('Rs. 104.00');
  await scan.press('F12');
  const pay = page.getByTestId('pos-pay');
  const delivery = pay.getByTestId('pay-delivery');
  await expect(delivery.getByRole('combobox', { name: 'How the goods go', exact: true })).toHaveValue('LORRY');
  await expect(delivery.getByRole('combobox', { name: 'Lorry service', exact: true })).toHaveText(new RegExp(transporterName));
  await expect(delivery.getByLabel('Deliver to')).toHaveValue('7 Town Hall Road, Madurai');
  await pay.getByRole('button', { name: 'Complete bill (Enter)' }).click();
  const done = page.getByTestId('pos-done');
  await expect(done.getByTestId('pos-delivery')).toHaveText(`Lorry service: ${transporterName} to Madurai branch, Madurai. Goes to dispatch.`);
  await done.getByRole('button', { name: 'New bill (Enter)' }).click();

  // Close the shift: Rs. 500 float + Rs. 104 cash.
  await page.getByRole('button', { name: 'Close shift' }).click();
  const close = page.getByTestId('pos-close-shift');
  await close.getByLabel('Number of Rs. 500', { exact: true }).fill('1');
  await close.getByLabel('Number of Rs. 100', { exact: true }).fill('1');
  await close.getByLabel('Number of Rs. 2', { exact: true }).fill('2');
  await close.getByRole('button', { name: 'Close shift with Rs. 604.00' }).click();
  await expect(close.getByTestId('shift-difference')).toHaveText('None');

  // Packing: the owner picks; someone else checks; the owner packs it in two cartons. No prices on the challan.
  await page.goto(`${apps.billing}/dispatch/packing`);
  await page.getByTestId('challans-table').getByRole('row', { name: new RegExp(debtorName) }).getByRole('button').click();
  let challan = page.getByTestId('challan-card');
  await expect(challan.getByTestId('challan-lines')).toContainText(itemName);
  await expect(challan.getByTestId('challan-lines')).not.toContainText('Rs.');
  await challan.getByTestId('pick-form').getByRole('button', { name: 'Record picking' }).click();
  await expect(challan.getByTestId('challan-progress')).toHaveText('To check');
  const checker = await checkOnAnotherPc(browser, page, debtorName, stamp);
  await page.goto(`${apps.billing}/dispatch/packing`);
  await page.getByTestId('challans-table').getByRole('row', { name: new RegExp(debtorName) }).getByRole('button').click();
  challan = page.getByTestId('challan-card');
  const pack = challan.getByTestId('pack-form');
  await pack.getByLabel('Packages').fill('2');
  await pack.getByRole('button', { name: 'Record packing' }).click();
  await expect(challan.getByTestId('challan-progress')).toHaveText('Packed');
  await expect(challan).toContainText(`${owner.name} / ${checker} / ${owner.name}`);
  await expect(challan.getByRole('link', { name: 'Package labels (2)' })).toBeVisible();

  // Dispatch books it: LR, two packages, Rs. 250 freight to pay at Madurai.
  await page.goto(`${apps.billing}/dispatch`);
  const row = page.getByTestId('dispatch-queue').getByRole('row', { name: new RegExp(debtorName) });
  await expect(row).toContainText(`Lorry service: ${transporterName} to Madurai branch, Madurai`);
  await row.getByRole('checkbox').check();
  const form = page.getByTestId('dispatch-form');
  await expect(form.getByRole('combobox', { name: 'Booked at', exact: true })).toHaveText(/Gandhipuram office, Coimbatore/);
  await form.getByLabel('Packages').fill('2');
  await form.getByLabel('LR/GR number').fill(lr);
  await form.getByLabel('Freight amount (Rs.)').fill('250');
  await form.getByRole('button', { name: 'Record dispatch' }).click();
  await expect(page.getByTestId('dispatch-queue')).not.toContainText(debtorName);

  const register = page.getByTestId('consignments-table');
  await page.getByLabel('Find a dispatch').fill(lr);
  await page.getByRole('button', { name: 'Search' }).click();
  const booked = register.getByRole('row', { name: new RegExp(lr) });
  await expect(booked).toContainText(debtorName);
  await expect(booked).toContainText(`${lr} of ${localDate(0)}`);
  await expect(booked).toContainText('To pay Rs. 250.00');
  await expect(booked).toContainText(localDate(2));
  await expect(booked).toContainText(/MAIN\/DSP\/\d{6}/);

  // The customer received it.
  await booked.getByRole('button', { name: 'Report delivery' }).click();
  const report = register.getByTestId('delivery-form');
  await report.getByLabel('Received by / why not delivered').fill('Received by Selvam');
  await report.getByRole('button', { name: 'Save delivery' }).click();
  await expect(booked).toContainText(`Delivered on ${localDate(0)} (Received by Selvam)`);
});
