import { test as base, expect } from '@playwright/test';

export interface AuthUser {
  stravaAthleteId: number;
  displayName: string;
}

/** Identity seeded by auth.setup.ts (/auth/test-login) and restored from storageState. */
export const SEED_USER: AuthUser = {
  stravaAthleteId: 12345,
  displayName: 'Test Rider',
};

// storageState (configured in playwright.config.ts) injects a REAL session cookie,
// so — unlike the stub variant — we do NOT mock /api/auth/me here. Tests hit the
// real backend already authenticated. The fixture only lands on the dashboard so the
// seed (and generated tests) can keep an empty body.
export const test = base.extend({
  page: async ({ page }, use) => {
    await page.goto('/dashboard');
    await use(page);
  },
});

export { expect };
