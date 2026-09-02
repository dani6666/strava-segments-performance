import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  use: {
    baseURL: 'http://localhost:4200', // ng serve — lets page.goto('/dashboard') work
    trace: 'on-first-retry',
  },
  // Frontend dev server. The BACKEND must also run in the E2E environment
  // (ASPNETCORE_ENVIRONMENT=E2E, so /auth/test-login is mapped) with its Postgres
  // available. Once a test DB is wired, add a second webServer entry, e.g.:
  //   { command: 'dotnet run', cwd: '../strava-segments-performance-backend',
  //     url: 'http://localhost:5000/health',
  //     env: { ASPNETCORE_ENVIRONMENT: 'E2E' }, reuseExistingServer: !process.env.CI }
  webServer: {
    command: 'npm start',
    url: 'http://localhost:4200',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
  projects: [
    // Logs in once via test-login and saves storageState to playwright/.auth/user.json.
    { name: 'setup', testMatch: /auth\.setup\.ts/ },
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'], storageState: 'playwright/.auth/user.json' },
      dependencies: ['setup'], // waits for 'setup' — every test starts authenticated
    },
  ],
});
