import { test as setup, expect } from '@playwright/test';
import { SEED_USER } from './fixtures';

// Backend origin — overridable in CI via an environment variable.
const BACKEND = process.env['E2E_API_BASE_URL'] ?? 'http://localhost:5000';

// storageState lands here; the file is ephemeral and gitignored — regenerated every
// run by this 'setup' project (see playwright.config.ts).
const authFile = 'playwright/.auth/user.json';

setup('authenticate', async ({ page }) => {
  // Call the backend's E2E-only seam (/auth/test-login). The backend runs SignInAsync
  // and sets a REAL httpOnly session cookie — no Strava. Requires the backend running
  // with ASPNETCORE_ENVIRONMENT=E2E.
  //
  // page.request shares the browser context's cookie jar, so the cookie lands where
  // storageState() can capture it.
  const res = await page.request.get(`${BACKEND}/auth/test-login`, {
    params: { athleteId: SEED_USER.stravaAthleteId, name: SEED_USER.displayName },
  });
  expect(res.ok()).toBeTruthy();

  await page.context().storageState({ path: authFile });
});
