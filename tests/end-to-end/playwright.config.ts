import { defineConfig, devices } from '@playwright/test';
import path from 'node:path';

// Browser -> Next.js -> API -> PostgreSQL tests on an isolated stack:
//   API on :5181 against a freshly recreated supermarketbilling_e2e database (scripts/run-api-e2e.ps1)
//   web apps on :3100-3103, built into .next-e2e so they can run next to development servers.
// The development database and servers are never touched. Docker (PostgreSQL) must be running.
const repoRoot = path.resolve(__dirname, '..', '..');
const apiUrl = 'http://localhost:5181';

const webApps = [
  { name: 'billing-web', port: 3100 },
  { name: 'owner-dashboard', port: 3101 },
  { name: 'collection-app', port: 3102 },
  { name: 'owner-archive-web', port: 3103 },
];

export default defineConfig({
  testDir: './specs',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: 0,
  workers: 1,
  timeout: 90_000,
  expect: { timeout: 15_000 },
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'setup', testMatch: /01-setup\.spec\.ts/, use: { ...devices['Desktop Chrome'] } },
    { name: 'desktop-chromium', dependencies: ['setup'], testIgnore: /(01-setup|collection-app)\.spec\.ts/, use: { ...devices['Desktop Chrome'] } },
    { name: 'phone-chromium', dependencies: ['setup'], testMatch: /collection-app\.spec\.ts/, use: { ...devices['Pixel 7'] } },
  ],
  webServer: [
    {
      command: `powershell -NoProfile -ExecutionPolicy Bypass -File "${path.join(repoRoot, 'scripts', 'run-api-e2e.ps1')}"`,
      url: `${apiUrl}/health/ready`,
      reuseExistingServer: false,
      timeout: 240_000,
      cwd: repoRoot,
    },
    {
      // Counter agent with file "devices" and a simulated scale (scripts/run-counter-agent-e2e.ps1).
      command: `powershell -NoProfile -ExecutionPolicy Bypass -File "${path.join(repoRoot, 'scripts', 'run-counter-agent-e2e.ps1')}"`,
      url: 'http://127.0.0.1:47800/status', // answers 403 without the billing origin: that still means it is up
      reuseExistingServer: false,
      timeout: 180_000,
      cwd: repoRoot,
    },
    ...webApps.map((app) => ({
      command: `npm exec --workspace apps/${app.name} -- next dev --port ${app.port}`,
      url: `http://localhost:${app.port}`,
      reuseExistingServer: false,
      timeout: 180_000,
      cwd: repoRoot,
      env: { NEXT_TELEMETRY_DISABLED: '1', API_INTERNAL_URL: apiUrl, NEXT_DIST_DIR: '.next-e2e' },
    })),
  ],
});
