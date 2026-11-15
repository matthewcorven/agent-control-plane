import { test, expect, waitForInteractive, waitForWebAssembly, clickFluentButton, fluentButton } from '../fixtures';
import type { Page } from '@playwright/test';

async function selectModel(page: Page, displayName: string) {
  const picker = page.getByRole('combobox', { name: 'Conversation model' });
  await picker.click();
  await page.getByText(displayName, { exact: true }).click();
  await expect(picker).toContainText(displayName);
}

for (const renderer of ['Server', 'WebAssembly']) {
  for (const path of ['happy', 'alternate', 'edge'] as const) {
    test(`${renderer} ${path}: conversation settings, correlated provider metrics and next-run selection`, async ({ page, backend, fork }) => {
      test.setTimeout(120000);
      const first = await backend.createModel();
      const second = await backend.createModel();
      try {
        await page.addInitScript(({ showModelInfo }) => {
          localStorage.setItem('agui-chat-settings', JSON.stringify({
            thinkingEffort: 2, temperature: 0.25, topP: 0.5, topK: 17, numCtx: 4096,
            monitoringDisplay: 2, monitoring: {
              showModelInfo, showTokens: true, showDurations: true, showThroughput: true,
              showAdvancedMetrics: true,
            },
          }));
        }, { showModelInfo: path !== 'alternate' });
        await page.goto('/chat');
        await waitForInteractive(page);
        if (renderer === 'WebAssembly') {
          await waitForWebAssembly(page);
          await page.goto('/chat');
          await waitForInteractive(page);
        }
        await expect(page.locator('.chat-page')).toHaveAttribute('data-renderer', renderer);
        await expect(page.locator('.chat-page')).toHaveAttribute('data-browser', String(renderer === 'WebAssembly'));
        await fork.capture(page, 'chat-ready');
        const active = path === 'happy' ? page : await fork.open('chat-ready', { cold: renderer === 'Server' });
        if (active !== page) {
          if (renderer === 'Server') {
            const settings = await page.evaluate(() => localStorage.getItem('agui-chat-settings'));
            await active.addInitScript(settings => {
              if (settings !== null) localStorage.setItem('agui-chat-settings', settings);
            }, settings);
          }
          await active.goto('/chat');
          await waitForInteractive(active);
          await expect(active.locator('.chat-page')).toHaveAttribute('data-renderer', renderer);
        }
        await selectModel(active, first.displayName);
        const script = await backend.createChatScript([{ messageId: 'answer', text: 'Telemetry answer without reasoning.' }]);
        if (path === 'edge') await backend.deleteModel(first.id);
        let releaseResponse = () => {};
        let responseReady = Promise.resolve();
        if (path === 'happy') {
          let markReady = () => {};
          responseReady = new Promise<void>(resolve => { markReady = resolve; });
          const release = new Promise<void>(resolve => { releaseResponse = resolve; });
          await active.route('**/ag-ui', async route => {
            const response = await route.fetch();
            markReady();
            await release;
            await route.fulfill({ response });
          }, { times: 1 });
        }
        const request = active.waitForRequest(r => new URL(r.url()).pathname === '/ag-ui' && r.method() === 'POST');
        await active.locator('.chat-input textarea').fill(script.prompt);
        await active.locator('.chat-input textarea').press('Enter');
        const input = (await request).postDataJSON();
        expect(input.state).toMatchObject({
          modelId: first.id, thinkingEffort: 'High', temperature: 0.25, topP: 0.5, topK: 17, numCtx: 4096,
        });
        expect(input.state.telemetryChannel).toMatch(/^[a-f0-9]{32}$/);
        expect(input.runId).toBeTruthy();
        expect(input.threadId).toBeTruthy();
        if (path === 'happy') {
          try {
            await responseReady;
            await expect(active.locator('.monitoring-container')).toContainText(input.runId);
            await expect(active.locator('.assistant-message')).not.toContainText('Telemetry answer without reasoning.');
          } finally {
            releaseResponse();
          }
        }
        if (path === 'edge') {
          await expect(active.locator('.message-status')).toContainText('could not be completed');
          expect((await backend.chatScriptStatus(script.id)).calls).toBe(0);
          await expect(active.locator('.monitoring-container')).toHaveCount(0);
        } else {
          await expect(active.locator('.assistant-message')).toContainText('Telemetry answer without reasoning.');
          expect((await backend.chatScriptStatus(script.id)).calls).toBe(1);
          const monitoring = active.locator('.monitoring-container');
          await expect(monitoring).toHaveCount(1);
          await expect(monitoring).toContainText(input.runId);
          await expect(monitoring).toContainText('Total tokens');
          await expect(monitoring).toContainText('Overall');
          if (path === 'happy') {
            await expect(monitoring.locator('.monitoring-model')).toContainText('Provider');
            await expect(monitoring.locator('.monitoring-model')).toContainText('Ollama');
            await expect(monitoring.locator('.monitoring-model')).toContainText(first.modelId);
          } else {
            await expect(monitoring.locator('.monitoring-model')).toHaveCount(0);
          }
        }
        await selectModel(active, second.displayName);
        const nextRequest = active.waitForRequest(r => new URL(r.url()).pathname === '/ag-ui' && r.method() === 'POST');
        await active.locator('.chat-input textarea').fill('Next-run circuit canary');
        await clickFluentButton(active.locator('.send-button'));
        const next = (await nextRequest).postDataJSON();
        expect(next.state.modelId).toBe(second.id);
        expect(next.runId).not.toBe(input.runId);
        if (path === 'edge') expect(next.threadId).not.toBe(input.threadId);
        else expect(next.threadId).toBe(input.threadId);
        await expect(active.locator('.assistant-message').last()).toContainText('E2E echo: Next-run circuit canary');
        await expect(active.locator('.monitoring-container').last()).toContainText(next.runId);
        if (path === 'happy') {
          await expect(active.locator('.monitoring-model').first()).toContainText(first.modelId);
          await expect(active.locator('.monitoring-model').last()).toContainText(second.modelId);
        }
        await clickFluentButton(fluentButton(active, 'New chat'));
        await expect(active.locator('.assistant-message')).toHaveCount(0);
        await expect(active.getByRole('combobox', { name: 'Conversation model' })).toContainText('Server default');
        await expect(active.locator('#blazor-error-ui')).not.toBeVisible();
        const stored = await backend.listModels();
        expect(stored.find(m => m.id === second.id)?.modelId).toBe(second.modelId);
      } finally {
        await backend.deleteModel(first.id);
        await backend.deleteModel(second.id);
      }
    });
  }
}
