import { request } from '@playwright/test';
import { SEED_USER } from './fixtures';

// Wipe the fitness-trend fixture inserted by auth.setup.ts. Runs once after the whole
// suite, regardless of test outcome. The browser context (and its cookie jar) is gone
// by the time this fires, so we start a fresh request context, re-authenticate via
// /auth/test-login to get a session cookie, then hit /e2e/reset.
//
// A failed teardown must NOT throw — it would mask real test failures. We log and move on.
export default async function globalTeardown(): Promise<void> {
  const backend = process.env['E2E_API_BASE_URL'] ?? 'http://localhost:5000';
  const ctx = await request.newContext({ baseURL: backend });
  try {
    const loginRes = await ctx.get('/auth/test-login', {
      params: { athleteId: SEED_USER.stravaAthleteId, name: SEED_USER.displayName },
    });
    if (!loginRes.ok()) {
      console.warn(
        `[global-teardown] /auth/test-login returned ${loginRes.status()}; skipping reset.`,
      );
      return;
    }
    const resetRes = await ctx.post('/e2e/reset');
    if (!resetRes.ok()) {
      console.warn(`[global-teardown] /e2e/reset returned ${resetRes.status()}.`);
    }
  } catch (err) {
    console.warn(
      `[global-teardown] error during reset: ${err instanceof Error ? err.message : String(err)}`,
    );
  } finally {
    await ctx.dispose();
  }
}
