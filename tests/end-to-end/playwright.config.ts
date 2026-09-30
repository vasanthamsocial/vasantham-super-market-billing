import { defineConfig, devices } from '@playwright/test';
import path from 'node:path';

// Full browser -> Next.js -> API -> PostgreSQL tests. The database must be running (scripts/db-up.ps1)
// and migrated (scripts/db-migrate.ps1). Servers already running on these ports are reused.
const repoRoot = path.resolve(__dirname, '..', '..');
const powershell = 'powershell -NoProfile -ExecutionPolicy Bypass -File';

const webApps = [
  { name: 'billing-web', port: 3000 },
  { name: 'owner-dashboard', port: 3001 },
  { name: 'collection-app', port: 3002 },
  { name: 'owner-archive-web', port: 3003 },
];

export default defineConfig({
  testDir: './specs',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: 0,
  workers: 1,
  timeout: 60_000,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'desktop-chromium', use: { ...devices['Desktop Chrome'] }, testIgnore: /collection-app/ },
    { name: 'phone-chromium', use: { ...devices['Pixel 7'] }, testMatch: /collection-app/ },
  ],
  webServer: [
    {
      command: `${powershell} "${path.join(repoRoot, 'scripts', 'run-api.ps1')}"`,
      url: 'http://localhost:5080/health/live',
      reuseExistingServer: true,
      timeout: 120_000,
      cwd: repoRoot,
    },
    ...webApps.map((app) => ({
      // Start directly (not via run-web.ps1) so the archive app can be tested in its disabled state.
      command: `npm run dev --workspace apps/${app.name}`,
      url: `http://localhost:${app.port}`,
      reuseExistingServer: true,
      timeout: 180_000,
      cwd: repoRoot,
      env: { NEXT_TELEMETRY_DISABLED: '1', API_INTERNAL_URL: 'http://localhost:5080' },
    })),
  ],
});
