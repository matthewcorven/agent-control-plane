import { test, expect, waitForInteractive } from '../fixtures';

for (const scenario of [
  { route: 'harness-planning', tool: 'todos_get_all', args: {}, expected: '' },
  { route: 'harness-assistant', tool: 'meal_ideas', args: { request: 'lentil dinner' }, expected: 'lentil dinner' },
] as const) {
  test(`${scenario.route} executes and restores a real Harness tool result`, async ({ page, backend }) => {
    const model = await backend.createModel({ supportsTools: true });
    try {
      const script = await backend.createChatScript([
        { messageId: 'tool', tool: scenario.tool, arguments: scenario.args },
        { messageId: 'answer', text: 'Harness tool finished.' },
      ]);
      await page.goto(`/${scenario.route}?modelId=${model.id}`);
      await waitForInteractive(page);
      const input = page.getByRole('textbox', { name: 'Scenario message' });
      await input.fill(script.prompt);
      await input.press('Enter');
      await expect(page.locator('.scenario-transcript')).toContainText('Harness tool finished.');
      await expect(page.locator('.tool-block-complete')).toContainText(scenario.tool);
      await expect(page.locator('.scenario-error')).toHaveCount(0);
      const status = await backend.chatScriptStatus(script.id);
      expect(status.calls).toBe(2);
      expect(status.results).toHaveLength(1);
      expect(status.results[0].length).toBeGreaterThan(0);
      expect(status.results[0]).not.toMatch(/error|exception|rejected/i);
      if (scenario.expected) expect(status.results[0]).toContain(scenario.expected);
      await page.reload();
      await waitForInteractive(page);
      await expect(page.locator('.tool-block-complete')).toContainText(scenario.tool);
      await expect(page.locator('.scenario-error')).toHaveCount(0);
    } finally { await backend.deleteModel(model.id); }
  });
}
