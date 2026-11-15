import { test, expect } from '@playwright/test';

test('local A2A streaming, background and MCP workflows', async ({ page }) => {
  await page.goto('/local-protocols');
  const status = page.getByRole('status');
  await page.getByLabel('Message to the echo agent').fill('Browser protocol test');
  await page.getByRole('button', { name: 'Run A2A stream' }).click();
  await expect(status).toContainText('Artifact: Echo: Browser protocol test', { timeout: 30000 });
  await expect(status).toContainText('Discovered:');
  await page.getByRole('button', { name: 'Run A2A background task' }).click();
  await expect(status).toContainText('Completed: Echo: Browser protocol test', { timeout: 30000 });
  await page.getByRole('button', { name: 'Discover and call MCP tool' }).click();
  await expect(status).toContainText('Tools: add_numbers', { timeout: 30000 });
  await expect(status).toContainText('Result: 42');
});
