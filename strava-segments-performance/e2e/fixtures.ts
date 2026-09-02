import { test as base, expect } from '@playwright/test';

export interface AuthUser {
  stravaAthleteId: number;
  displayName: string;
}

/** Jedna tożsamość dla wszystkich testów. Nazwa pojawia się w nagłówku "Welcome, …". */
export const SEED_USER: AuthUser = {
  stravaAthleteId: 12345,
  displayName: 'Test Rider',
};

// Override'ujemy wbudowany fixture `page` — dzięki temu ciało seeda (i każdego
// wygenerowanego testu) startuje od zalogowanego dashboardu, bez powtarzania setupu.
export const test = base.extend({
  page: async ({ page }, use) => {
    // authGuard woła GET /api/auth/me — stub przepuszcza go bez prawdziwego OAuth.
    // (test-plan §4: NIGDY prawdziwa Strava — rate limity, CAPTCHA, AGENTS.md.)
    await page.route('**/api/auth/me', (route) => route.fulfill({ json: SEED_USER }));

    // ngOnInit woła GET /api/workouts/fetch-status — 'idle' to najprostszy,
    // deterministyczny stan startowy (żaden timer/polling się nie uruchamia).
    await page.route('**/api/workouts/fetch-status', (route) =>
      route.fulfill({
        json: {
          status: 'idle',
          stage: null,
          activitiesProcessed: 0,
          totalToProcess: null,
          errorMessage: null,
        },
      }),
    );

    await page.goto('/dashboard');
    await use(page);
  },
});

export { expect };
