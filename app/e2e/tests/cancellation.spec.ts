import { test, expect, waitForInteractive, waitForWebAssembly, clickFluentButton, fluentButton, resetScenario } from '../fixtures';

for (const renderer of ['Server', 'WebAssembly'] as const) {
  for (const action of ['Stop', 'reset', 'navigation', 'completion'] as const) {
    test(`tool continuation cancellation: ${renderer} ${action} preserves execution and a live renderer`, async ({ page, backend, fork }) => {
      test.setTimeout(120000);
      const model = await backend.createModel({ supportsTools: true });
      try {
        await page.goto(`/generative-ui?modelId=${model.id}`);
        await waitForInteractive(page);
        await page.getByRole('textbox', { name: 'Scenario message' }).fill('Predecessor canary');
        await page.getByRole('textbox', { name: 'Scenario message' }).press('Enter');
        await expect(page.locator('.scenario-transcript')).toContainText('E2E echo: Predecessor canary');
        await expect(page.locator('.scenario-pending')).toHaveCount(0);
        await fork.capture(page, 'continuation-ready');
        const view = action === 'Stop' && renderer === 'Server' ? page :
          await fork.open('continuation-ready', { cold: renderer === 'Server' });
        if (renderer === 'WebAssembly') await waitForWebAssembly(view);
        if (view !== page) {
          await view.goto(`/generative-ui?modelId=${model.id}`);
          await waitForInteractive(view);
        }
        await expect(view.locator('.scenario-page')).toHaveAttribute('data-renderer', renderer);
        await expect(view.locator('.scenario-page')).toHaveAttribute('data-browser', String(renderer === 'WebAssembly'));
        const script = await backend.createChatScript([
          { messageId: 'create', tool: 'create_plan', arguments: { title: 'Cancellation plan', steps: ['Prepare', 'Deploy'] } },
          { messageId: 'update', tool: 'update_plan_step', arguments: { stepId: 'step-1', status: 'done' }, waitForRelease: true },
          { messageId: 'answer', text: 'Released continuation completed.' },
        ]);
        const started = view.waitForRequest(r => r.url().endsWith('/ag-ui/generative-ui') && r.method() === 'POST');
        await view.getByRole('textbox', { name: 'Scenario message' }).fill(script.prompt);
        await view.getByRole('textbox', { name: 'Scenario message' }).press('Enter');
        const request = await started;
        expect(new URL(request.url()).origin).toBe(new URL(view.url()).origin);
        await expect(view.locator('.plan-checklist')).toContainText('Cancellation plan');
        await expect(view.locator('[data-step="step-1"]')).toContainText('pending');
        await expect.poll(async () => {
          const status = await backend.chatScriptStatus(script.id);
          return { calls: status.calls, results: status.results.length, cancelled: status.cancelled };
        }).toEqual({ calls: 2, results: 1, cancelled: false });

        if (action === 'completion') {
          await backend.releaseChatScript(script.id);
          await expect(view.locator('[data-step="step-1"]')).toContainText('done');
          await expect(view.locator('.scenario-transcript')).toContainText('Released continuation completed.');
          await expect(view.locator('.scenario-pending')).toHaveCount(0);
          const status = await backend.chatScriptStatus(script.id);
          expect(status).toMatchObject({ calls: 3, cancelled: false });
          expect(status.results).toHaveLength(2);
          await expect(view.locator('.scenario-metrics')).toHaveAttribute('data-run', request.postDataJSON().runId);
        } else {
          const aborted = action === 'navigation' ? null : view.waitForEvent('requestfailed', r => r === request);
          if (action === 'Stop') {
            await clickFluentButton(fluentButton(view, 'Stop'));
            await expect(view.locator('.scenario-error')).toContainText('Response stopped.');
          } else if (action === 'reset') {
            await resetScenario(view);
          } else {
            await view.goto('/chat');
            await waitForInteractive(view);
          }
          if (aborted) expect((await aborted).failure()?.errorText).toBe('net::ERR_ABORTED');
          await expect.poll(async () => (await backend.chatScriptStatus(script.id)).cancelled).toBe(true);
          const status = await backend.chatScriptStatus(script.id);
          expect(status.calls).toBe(2);
          expect(status.results).toHaveLength(1);
          if (action !== 'navigation')
            await expect(view.locator('.scenario-transcript')).not.toContainText('Released continuation completed.');
        }
        if (action === 'navigation') {
          await view.locator('.chat-input textarea').fill('Navigation canary');
          await view.locator('.chat-input textarea').press('Enter');
          await expect(view.locator('.assistant-message')).toContainText('E2E echo: Navigation canary');
        } else {
          if (action !== 'reset') await resetScenario(view);
          await view.getByRole('textbox', { name: 'Scenario message' }).fill('Native renderer canary');
          await view.getByRole('textbox', { name: 'Scenario message' }).press('Enter');
          await expect(view.locator('.scenario-transcript')).toContainText('E2E echo: Native renderer canary');
        }
        await expect(view.locator('#blazor-error-ui')).not.toBeVisible();
      } finally {
        await backend.deleteModel(model.id);
      }
    });
  }
}
