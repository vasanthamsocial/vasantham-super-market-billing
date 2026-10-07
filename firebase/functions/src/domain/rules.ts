import { DomainError } from '../core/errors.js';

// Field rules of the identity and organisation records (identical to the .NET domain).

export const MinPasswordLength = 10;
export const MaxPasswordLength = 128;

export function normalizeUsername(username: string | null | undefined): string {
  const normalized = (username ?? '').trim().toLowerCase();
  if (!/^[a-z0-9][a-z0-9._-]{2,49}$/.test(normalized)) {
    throw new DomainError('user.username_invalid', 'Username must be 3-50 characters: letters, digits, dot, hyphen or underscore.');
  }
  return normalized;
}

/** Null when acceptable, otherwise the reason. */
export function passwordProblem(password: string, username: string): string | null {
  if (!password || password.length < MinPasswordLength) return `Password must be at least ${MinPasswordLength} characters.`;
  if (password.length > MaxPasswordLength) return `Password must be at most ${MaxPasswordLength} characters.`;
  if (password.toLowerCase().includes(username.toLowerCase())) return 'Password must not contain the username.';
  if (new Set(password).size < 4) return 'Password is too simple.';
  return null;
}

export function displayName(value: string | null | undefined): string {
  const trimmed = value?.trim();
  if (!trimmed || trimmed.length > 100) throw new DomainError('user.display_name_invalid', 'Display name is required (max 100 characters).');
  return trimmed;
}

export function required(value: string | null | undefined, code: string, message: string, maxLength: number): string {
  const trimmed = value?.trim();
  if (!trimmed || trimmed.length > maxLength) throw new DomainError(code, message);
  return trimmed;
}

export function optional(value: string | null | undefined, code: string, message: string, maxLength: number): string | null {
  return !value || !value.trim() ? null : required(value, code, message, maxLength);
}

export function businessCode(code: string | null | undefined): string {
  const normalized = (code ?? '').trim().toUpperCase();
  if (!/^[A-Z0-9]{2,12}$/.test(normalized)) throw new DomainError('business.code_invalid', 'Business code must be 2-12 letters or digits.');
  return normalized;
}

export function storeCode(code: string | null | undefined): string {
  const normalized = (code ?? '').trim().toUpperCase();
  if (!/^[A-Z0-9][A-Z0-9-]{0,11}$/.test(normalized)) throw new DomainError('store.code_invalid', 'Store code must be 1-12 letters, digits or hyphens.');
  return normalized;
}

export function companyCode(code: string | null | undefined): string {
  const normalized = (code ?? '').trim().toUpperCase();
  if (!/^[A-Z0-9]{3,20}$/.test(normalized)) throw new DomainError('tenant.code_invalid', 'Company code must be 3-20 letters or digits.');
  return normalized;
}

export function stateCode(value: string | null | undefined): string {
  const normalized = (value ?? '').trim();
  if (!/^[0-9]{2}$/.test(normalized) || normalized === '00') {
    throw new DomainError('state_code.invalid', 'State code must be the two-digit GST state code, for example 33.');
  }
  return normalized;
}

// GSTIN: 2-digit state code, 10-character PAN, entity number, 'Z', and a mod-36 check character.

const gstinAlphabet = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ';

export function gstinCheckCharacter(first14: string): string {
  let sum = 0;
  for (let i = 0; i < first14.length; i++) {
    const product = gstinAlphabet.indexOf(first14[i]!) * ((i % 2) + 1);
    sum += Math.floor(product / 36) + (product % 36);
  }
  return gstinAlphabet[(36 - (sum % 36)) % 36]!;
}

export function gstinValid(value: string | null | undefined): boolean {
  return !!value && /^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$/.test(value) && value[14] === gstinCheckCharacter(value.slice(0, 14));
}

export function gstinComplete(first14: string): string {
  return first14 + gstinCheckCharacter(first14);
}

export function validateGstin(value: string, expectedState: string): string {
  const normalized = value.trim().toUpperCase();
  if (!gstinValid(normalized)) throw new DomainError('gstin.invalid', `'${normalized}' is not a valid GSTIN (format or check digit is wrong).`);
  if (normalized.slice(0, 2) !== expectedState) {
    throw new DomainError('gstin.state_mismatch', `GSTIN ${normalized} belongs to state ${normalized.slice(0, 2)}, but the state code entered is ${expectedState}.`);
  }
  return normalized;
}

export const TaxModes = { GstRegular: 'GST_REGULAR', GstComposition: 'GST_COMPOSITION', NotGstRegistered: 'NOT_GST_REGISTERED' } as const;
export const AllTaxModes: readonly string[] = Object.values(TaxModes);

export function validateTaxMode(mode: string): string {
  if (!AllTaxModes.includes(mode)) throw new DomainError('tax_mode.unknown', `Unknown tax registration mode '${mode}'.`);
  return mode;
}

/** The registration entry's GSTIN: required and valid for the GST modes, none when not registered. */
export function registrationGstin(mode: string, gstin: string | null): string | null {
  validateTaxMode(mode);
  const normalized = gstin?.trim() ? gstin.trim().toUpperCase() : null;
  if (mode === TaxModes.NotGstRegistered) return null;
  if (!normalized || !gstinValid(normalized)) throw new DomainError('tax_mode.gstin_required', 'GST Regular and Composition need the business\'s valid GSTIN.');
  return normalized;
}

/** Units every new business starts with. */
export const DefaultUnits: ReadonlyArray<[code: string, name: string, decimals: number]> = [
  ['PCS', 'Pieces', 0], ['KG', 'Kilogram', 3], ['G', 'Gram', 0], ['L', 'Litre', 3], ['ML', 'Millilitre', 0],
  ['M', 'Metre', 2], ['BOX', 'Box', 0], ['PKT', 'Packet', 0], ['DZN', 'Dozen', 0], ['BAG', 'Bag', 0],
];
