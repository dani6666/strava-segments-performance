import { test, expect } from './fixtures';

test('seed', async ({ page }) => {
  // Fixture już zastubował auth i wszedł na /dashboard.
  // To jedno asercja NIE testuje logiki biznesowej — jest kotwicą stanu startowego
  // i wzorem lokatorów (getByRole), który Generator ma kopiować do każdego testu.
  await expect(page.getByRole('heading', { name: /welcome/i })).toBeVisible();
});
