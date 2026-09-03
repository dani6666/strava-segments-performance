import { test, expect } from '@playwright/test';

// Vertical-slice happy-path e2e (test-plan.md §3 Phase 5, Risk #7).
//
// Composed slice proven end-to-end against the real backend chain:
//   1. Authenticated user lands on /dashboard.
//   2. Auto-analysis fires on fetch-status=completed → GET /api/analysis/fitness-trend
//      returns 2 points (Phase 1 seed shape: 2 activities × 3 shared segments).
//   3. User narrows the picker's "To" to a date that brackets only the earlier activity.
//   4. Debounced re-trigger fires GET /api/analysis/fitness-trend?from=&to= → 1 point.
//
// Assertions are structural (response body length + canvas visible), never pixel-based.
// Synchronization uses page.waitForResponse — no waitForTimeout.
//
// This spec imports 'test' directly from @playwright/test (not from './fixtures') so the
// fixture's implicit page.goto('/dashboard') does not fire before the initial-analysis
// waitForResponse is registered — Promise.all here guarantees the wait wins the race.
test('picker narrows fitness-trend chart end-to-end', async ({ page }) => {
  const initialWait = page.waitForResponse(
    (r) => r.url().includes('/api/analysis/fitness-trend') && r.status() === 200,
  );
  const [initial] = await Promise.all([initialWait, page.goto('/dashboard')]);
  const initialBody = (await initial.json()) as unknown[];
  expect(initialBody.length).toBe(2);

  const canvas = page.locator('app-fitness-trend-chart canvas');
  await expect(canvas).toBeVisible();

  // Narrow "To" to a date that includes only the earlier seeded activity
  // (2026-08-15 vs 2026-08-22). Playwright's fill() on <input type="date"> takes ISO YYYY-MM-DD.
  const filteredWait = page.waitForResponse(
    (r) => r.url().includes('/api/analysis/fitness-trend') && r.status() === 200,
  );
  await page.getByLabel('To').fill('2026-08-15');
  const filtered = await filteredWait;
  const filteredBody = (await filtered.json()) as unknown[];
  expect(filteredBody.length).toBe(1);

  await expect(canvas).toBeVisible();
});
