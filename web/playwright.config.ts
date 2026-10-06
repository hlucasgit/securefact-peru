import { defineConfig, devices } from '@playwright/test'

// End-to-end tests of the web interface against a real API (ADR-040). The API must be running (default http://localhost:5180) with Sunat:Environment=Sandbox and the workers
// next to it; SF_E2E_ADMIN_EMAIL and SF_E2E_ADMIN_PASSWORD name a platform administrator, who creates a fresh tenant for every test file. See web/README.md.
const baseURL = process.env.SF_E2E_WEB_URL ?? 'http://localhost:5173'

export default defineConfig({
  testDir: 'e2e',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 2 : 4,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : [['list']],
  globalSetup: './e2e/global-setup.ts',
  use: { baseURL, trace: 'retain-on-failure', screenshot: 'only-on-failure', locale: 'es-PE', timezoneId: 'America/Lima', acceptDownloads: true },
  projects: [
    { name: 'chromium', testIgnore: /mobile\.spec\.ts/, use: { ...devices['Desktop Chrome'] } },
    { name: 'mobile', testMatch: /mobile\.spec\.ts/, use: { ...devices['Pixel 7'] } },
  ],
  webServer: process.env.SF_E2E_WEB_URL ? undefined : { command: 'npm run build && npm run preview', url: baseURL, reuseExistingServer: !process.env.CI, timeout: 120_000 },
})
