import { test, expect } from '@playwright/test';

test('starts a real two-ply game and shows attributed A2A results', async ({ page }) => {
  await page.goto('/chess');
  await expect(page.getByRole('heading', { name: 'Chess: coordinator and A2A workers' })).toBeVisible();
  await expect(page.getByRole('gridcell')).toHaveCount(64);
  await expect(page.getByText('Coordinator: Not started')).toBeVisible();
  const initialBoard = await page.getByRole('gridcell').allTextContents();
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await expect(page.getByText('Move limit reached', { exact: true })).toBeVisible({ timeout: 180_000 });
  await expect(page.getByText('Coordinator: Game complete', { exact: true })).toBeVisible();
  await expect(page.getByText('White: A2A response received', { exact: true })).toBeVisible();
  await expect(page.getByText('Black: A2A response received', { exact: true })).toBeVisible();
  const coordinator = page.getByRole('region', { name: 'Coordinator session' });
  await expect(coordinator.locator('article')).toHaveCount(4);
  await expect(coordinator).toContainText('Delegate to White');
  for (const color of ['White', 'Black']) {
    await page.getByRole('button', { name: `${color}: A2A response received` }).click();
    const session = page.getByRole('region', { name: `${color} session` });
    await expect(session.locator('article')).toHaveCount(2);
    await expect(session.locator('article').first()).toContainText('Request');
    await expect(session.locator('article').last().locator('pre')).toContainText(/[a-h][1-8][a-h][1-8]/);
  }
  await page.getByRole('button', { name: 'Coordinator: Game complete' }).click();
  await expect(coordinator).toContainText('Delegate to White');
  expect(await page.getByRole('gridcell').allTextContents()).not.toEqual(initialBoard);
  await expect(page.getByText('FEN: rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('alert')).toHaveCount(0);
});
