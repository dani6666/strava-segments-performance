import { test, expect } from '@playwright/test';

// Risk #2 (context/foundation/test-plan.md): the Strava OAuth handshake round-trip must
// complete end-to-end — challenge -> callback -> code exchange -> session cookie -> redirect.
// This drives the FULL login from a logged-out browser against the in-process E2E stub Strava
// (backend Program.cs /e2e-stub/*), asserting the browser lands authenticated on /dashboard
// as the stub athlete. No real Strava is ever contacted.
//
// Modeled on e2e/seed.spec.ts (role-based locators, wait-for-state — no waitForTimeout).
// Runs in the 'chromium-noauth' project (no storageState, no 'setup' dependency) so it starts
// unauthenticated — it must NOT reuse the seeded session.

// The stub athlete minted by /e2e-stub/api/athlete (backend Program.cs). Distinct from the
// SEED_USER (12345) so this test can never be satisfied by a leaked seeded session.
// displayName is the athlete's Strava *username* — the Strava OAuth provider maps the Name
// claim from `username`, which OnCreatingTicket stores as DisplayName.
const STUB_ATHLETE = { stravaAthleteId: '99999', displayName: 'e2e_rider' };
const API_BASE = 'http://localhost:5000';

test.describe('OAuth handshake round-trip (Risk #2)', () => {
  test('logged-out user completes the Strava login and lands authenticated on the dashboard', async ({
    page,
  }) => {
    // Start logged out on the login page.
    await page.goto('/login');

    // Trigger the OAuth challenge. This navigates to the backend /auth/login, which 302s
    // through the stub authorize -> /auth/callback -> stub token + athlete exchange ->
    // session cookie -> {frontendOrigin}/dashboard.
    await page.getByRole('button', { name: /connect with strava/i }).click();

    // Wait for the full redirect chain to settle on the dashboard (state, not time).
    await page.waitForURL('**/dashboard');

    // The dashboard renders authenticated content for the stub identity — proves the session
    // took AND that it belongs to the athlete the handshake produced (not just "any" session).
    await expect(
      page.getByRole('heading', { name: new RegExp(`welcome, ${STUB_ATHLETE.displayName}`, 'i') }),
    ).toBeVisible();

    // The session is a real backend cookie session: /api/auth/me returns the stub athlete.
    // page.request shares the browser's cookie jar, so the httpOnly session cookie is sent.
    const me = await page.request.get(`${API_BASE}/api/auth/me`);
    expect(me.ok()).toBeTruthy();
    expect(await me.json()).toMatchObject(STUB_ATHLETE);
  });
});
