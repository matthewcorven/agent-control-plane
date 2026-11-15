import { test, expect, waitForInteractive } from '../fixtures';

/**
 * Home / routing.
 *
 * The root route is a redirect, not a page: `Home.razor` navigates to `/chat` with
 * `replace: true`. These specs pin that contract because every other journey depends on it.
 */

test.describe('home redirect', () => {
  test('happy path: root redirects to the chat page', async ({ page }) => {
    await page.goto('/');
    await waitForInteractive(page);

    await expect(page).toHaveURL(/\/chat$/);
    await expect(page.getByRole('heading', { name: 'Chat', level: 1 })).toBeVisible();
  });

  test('alternate path: the redirect replaces history so Back does not bounce', async ({ page }) => {
    await page.goto('/');
    await waitForInteractive(page);
    await expect(page).toHaveURL(/\/chat$/);

    // A `replace: true` navigation leaves no entry to return to, so Back must leave the app
    // rather than land on `/` and redirect again.
    await page.goBack().catch(() => undefined);

    await expect(page).not.toHaveURL(/\/chat$/);
  });

  test('edge case: an unknown route renders the not-found page', async ({ page }) => {
    await page.goto('/this-route-does-not-exist');
    await waitForInteractive(page);

    await expect(page.getByText(/not found/i).first()).toBeVisible();
  });

  test('edge case: navigation menu reaches both top-level pages', async ({ page }) => {
    await page.goto('/chat');
    await waitForInteractive(page);

    // The nav renders twice: once in the desktop sidebar and once inside the mobile drawer.
    // Only one is visible at a given viewport, so scope to the visible link.
    const menu = page.locator('#main-menu').locator('visible=true');

    await menu.getByRole('link', { name: 'Models' }).click();
    await expect(page).toHaveURL(/\/models$/);

    await menu.getByRole('link', { name: 'Chat' }).click();
    await expect(page).toHaveURL(/\/chat$/);
  });
});
