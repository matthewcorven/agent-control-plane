import { test, expect, waitForInteractive, clickFluentButton } from '../fixtures';

test('text tasks stream over AG-UI and append produces a reviewed download', async ({ page }) => {
  await page.goto('/examples');
  await waitForInteractive(page);
  const workflow = page.locator('#example-mode');
  const input = page.getByRole('textbox', { name: 'Text or instruction' });
  const run = page.locator('fluent-button').filter({ hasText: 'Run' });

  await workflow.selectOption('summarize');
  await input.fill('a short story');
  await clickFluentButton(run);
  await expect(page.getByTestId('example-result')).toContainText('Summarize the following text:');
  await expect(page.getByTestId('example-result')).toContainText('a short story');

  await workflow.selectOption('append');
  await expect(page.getByTestId('example-result')).toHaveCount(0);
  await clickFluentButton(run);
  await expect(page.getByRole('alert')).toContainText('Select a text file');
  await page.locator('#example-file').setInputFiles({ name: 'input.txt', mimeType: 'text/plain', buffer: Buffer.from('original\n') });
  await input.fill('add a sentence');
  await clickFluentButton(run);
  await expect(page.getByTestId('example-result')).toContainText('add a sentence');
  const download = page.getByRole('link', { name: 'Download result as text' });
  await expect(download).toBeVisible();
  const [saved] = await Promise.all([page.waitForEvent('download'), download.click()]);
  const stream = await saved.createReadStream();
  const chunks: Buffer[] = [];
  for await (const chunk of stream) chunks.push(Buffer.from(chunk));
  expect(Buffer.concat(chunks).toString()).toContain('original\nE2E echo:');
});
