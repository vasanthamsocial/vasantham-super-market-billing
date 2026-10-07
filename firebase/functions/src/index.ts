import { defineSecret } from 'firebase-functions/params';
import { onRequest } from 'firebase-functions/v2/https';
import { createApp } from './app.js';

// Secrets are set once on the Firebase project (firebase functions:secrets:set ...); locally they come from .secret.local.
const setupCode = defineSecret('SB_SETUP_CODE');
const dataKey = defineSecret('SB_DATA_KEY');

const app = createApp();

/** The whole API, in Mumbai (asia-south1): https://asia-south1-<project>.cloudfunctions.net/api/... */
export const api = onRequest({ region: 'asia-south1', secrets: [setupCode, dataKey], memory: '512MiB', timeoutSeconds: 120, concurrency: 40 }, app);
