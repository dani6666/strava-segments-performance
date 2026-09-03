import { defineConfig, devices } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

// Resolve the E2E backend's Postgres connection string, in priority order:
//   1. E2E_DB_CONNECTION           — full override (CI sets this against its own Postgres service).
//   2. POSTGRES_USER/PASSWORD from the repo-root .env — the SAME creds docker-compose uses, so a
//      local `npm run test:e2e` connects to the running docker Postgres with no manual setup.
//   3. A non-secret local-dev default (a plain local Postgres per appsettings.Development.json).
// The E2E backend always targets a dedicated `strava_segments_e2e` database (EF creates it on
// migrate) so the run never touches dev data.
function readDotEnv(): Record<string, string> {
  for (const candidate of ['../.env', '.env']) {
    try {
      const raw = readFileSync(resolve(process.cwd(), candidate), 'utf8');
      const out: Record<string, string> = {};
      for (const line of raw.split(/\r?\n/)) {
        const trimmed = line.trim();
        if (!trimmed || trimmed.startsWith('#')) continue;
        const eq = trimmed.indexOf('=');
        if (eq === -1) continue;
        out[trimmed.slice(0, eq).trim()] = trimmed.slice(eq + 1).trim();
      }
      return out;
    } catch {
      // try the next candidate
    }
  }
  return {};
}

function resolveE2eDbConnection(): string {
  if (process.env['E2E_DB_CONNECTION']) return process.env['E2E_DB_CONNECTION'];
  const env = readDotEnv();
  const user = process.env['POSTGRES_USER'] ?? env['POSTGRES_USER'];
  const password = process.env['POSTGRES_PASSWORD'] ?? env['POSTGRES_PASSWORD'];
  if (user && password) {
    return `Host=localhost;Port=5432;Database=strava_segments_e2e;Username=${user};Password=${password}`;
  }
  return 'Host=localhost;Port=5432;Database=strava_segments_e2e;Username=strava_user;Password=strava_local_password';
}

export default defineConfig({
  testDir: './e2e',
  globalTeardown: './e2e/global-teardown.ts',
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: 'http://localhost:4200', // ng serve — lets page.goto('/dashboard') work
    trace: 'on-first-retry',
  },
  // Two servers: the Angular dev server, and the backend in the E2E environment (so
  // /auth/test-login and the /e2e-stub/* OAuth stub are mapped). reuseExistingServer is
  // off in CI; locally, stop any Development-mode stack on these ports first so Playwright
  // boots the E2E backend rather than reusing the wrong one.
  webServer: [
    {
      command: 'npm start',
      url: 'http://localhost:4200',
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
    },
    {
      command: 'dotnet run',
      cwd: '../strava-segments-performance-backend',
      url: 'http://localhost:5000/health',
      env: {
        ASPNETCORE_ENVIRONMENT: 'E2E',
        ASPNETCORE_URLS: 'http://localhost:5000',
        ConnectionStrings__DefaultConnection: resolveE2eDbConnection(),
      },
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
    },
  ],
  projects: [
    // Logs in once via test-login and saves storageState to playwright/.auth/user.json.
    { name: 'setup', testMatch: /auth\.setup\.ts/ },
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'], storageState: 'playwright/.auth/user.json' },
      dependencies: ['setup'], // waits for 'setup' — every test starts authenticated
      testIgnore: /oauth-handshake\.spec\.ts/, // the handshake test must start logged OUT
    },
    {
      // The OAuth handshake test drives the real login from a fresh, unauthenticated
      // context — no storageState, no 'setup' dependency.
      name: 'chromium-noauth',
      use: { ...devices['Desktop Chrome'] },
      testMatch: /oauth-handshake\.spec\.ts/,
    },
  ],
});
