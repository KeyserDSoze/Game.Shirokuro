import { defineConfig, devices } from '@playwright/test'

export default defineConfig({
  testDir: './tests',
  fullyParallel: true,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? 'github' : 'list',
  use: {
    baseURL: 'http://127.0.0.1:5173',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
  projects: [{
    name: 'chromium',
    use: {
      ...devices['Desktop Chrome'],
      // GitHub-hosted runners often have Chrome and its native libraries
      // preinstalled. Keep the bundled Playwright Chromium for local runs.
      ...(process.env.SHIROKURO_PLAYWRIGHT_USE_SYSTEM_CHROME === '1'
        ? { channel: 'chrome' as const }
        : {}),
    },
  }],
  webServer: [
    {
      command: 'dotnet run --no-launch-profile --project ../../src/backend/applications/Shirokuro.Api --urls http://127.0.0.1:5186',
      url: 'http://127.0.0.1:5186/api/health',
      env: { ASPNETCORE_ENVIRONMENT: 'Development' },
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
    },
    {
      command: 'npm run dev -- --host 127.0.0.1 --port 5173',
      url: 'http://127.0.0.1:5173',
      reuseExistingServer: !process.env.CI,
      timeout: 60_000,
    },
  ],
})


