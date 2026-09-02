import { test, expect } from './fixtures';

test('seed', async ({ page }) => {
  // The fixture already established the session (storageState) and navigated to
  // /dashboard. This single assertion does NOT test business logic — it is a landing
  // anchor and a style example (role-based locators) for the Generator to copy into
  // every generated test.
  await expect(page.getByRole('heading', { name: /welcome/i })).toBeVisible();
});
