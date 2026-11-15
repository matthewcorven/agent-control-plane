import { test, expect, waitForInteractive, waitForWebAssembly, resetScenario } from '../fixtures';

for (const cold of [true, false]) {
  test(`pending Auto bootstrap completes before ${cold ? 'cold' : 'warm-storage'} fork navigation and preserves browser transport`, async ({ page, backend, fork }, testInfo) => {
    test.setTimeout(120000);
    const model = await backend.createModel({ supportsTools: true });
    let releaseBootstrap: (() => void) | undefined;
    try {
      await page.goto(`/tools?modelId=${model.id}`);
      await waitForInteractive(page);
      await expect(page.locator('.scenario-page')).toHaveAttribute('data-renderer', 'Server');
      const input = page.getByRole('textbox', { name: 'Scenario message' });
      await input.fill('Before paced bootstrap');
      await input.press('Enter');
      await expect(page.locator('.scenario-transcript')).toContainText('E2E echo: Before paced bootstrap');
      const thread = await page.locator('.scenario-page').getAttribute('data-thread');
      if (!cold) {
        await waitForWebAssembly(page);
        await page.goto(`/tools?modelId=${model.id}`);
        await waitForInteractive(page);
      }
      await fork.capture(page, 'bootstrap');
      const view = await fork.open('bootstrap', { cold });
      let release!: () => void;
      const released = new Promise<void>(resolve => { release = resolve; });
      releaseBootstrap = release;
      let downloaded!: () => void;
      const downloadReady = new Promise<void>(resolve => { downloaded = resolve; });
      let heldResource: string | undefined;
      let bootReleased = false;
      const interrupted: string[] = [];
      const prematureNavigations: string[] = [];
      view.on('requestfailed', request => {
        if (request.url() === heldResource) interrupted.push(request.failure()?.errorText ?? 'unknown failure');
      });
      view.on('framenavigated', frame => {
        if (frame === view.mainFrame() && heldResource && !bootReleased)
          prematureNavigations.push(frame.url());
      });
      await view.route('**/_framework/Microsoft.FluentUI.AspNetCore.Components.Icons.Light.*.wasm', async route => {
        const response = await route.fetch();
        expect(response.status()).toBe(200);
        if (!heldResource) {
          heldResource = route.request().url();
          downloaded();
          await released;
        }
        await route.fulfill({ response });
      });
      await view.goto(`/tools?modelId=${model.id}`);
      await downloadReady;
      if (cold) {
        await waitForInteractive(view);
        await expect(view.locator('.scenario-page')).toHaveAttribute('data-renderer', 'Server');
      } else {
        await expect(view.locator('.loading-settings')).toBeVisible();
      }
      const started = Date.now();
      const readiness = waitForWebAssembly(view);
      try {
        // A live predecessor can advance while the fork's real boot response is pending.
        await resetScenario(page);
        await page.getByRole('textbox', { name: 'Scenario message' }).fill('Bootstrap release canary');
        await page.getByRole('textbox', { name: 'Scenario message' }).press('Enter');
        await expect(page.locator('.scenario-transcript')).toContainText('E2E echo: Bootstrap release canary');
      } finally {
        bootReleased = true;
        release();
        await readiness;
      }
      expect(Date.now() - started).toBeLessThan(60000);
      await testInfo.attach('bootstrap-navigation-evidence', {
        contentType: 'application/json',
        body: JSON.stringify({ heldResource, interrupted, prematureNavigations,
          bootstrapHash: await view.evaluate(() =>
            localStorage.getItem('blazor-resource-hash:AGUIWebChat.Client.Wasm')),
          phase: await view.locator('#spike-phase-value').innerText() }),
      });
      expect(prematureNavigations, 'Keep the document alive until its real bootstrap response is released').toEqual([]);
      expect(interrupted, 'Navigation must not cancel a pending real WASM bootstrap response').toEqual([]);
      await view.goto(`/tools?modelId=${model.id}`);
      await waitForInteractive(view);
      await expect(view.locator('.scenario-page')).toHaveAttribute('data-renderer', 'WebAssembly');
      await expect(view.locator('.scenario-page')).toHaveAttribute('data-browser', 'true');
      await expect(view.locator('.scenario-page')).toHaveAttribute('data-thread', thread!);
      await expect(view.locator('.scenario-transcript')).toContainText('E2E echo: Before paced bootstrap');
      const request = view.waitForRequest(r => r.url().endsWith('/ag-ui/tools') && r.method() === 'POST');
      await view.getByRole('textbox', { name: 'Scenario message' }).fill('After paced bootstrap');
      await view.getByRole('textbox', { name: 'Scenario message' }).press('Enter');
      const observed = await request;
      expect(new URL(observed.url()).origin).toBe(new URL(view.url()).origin);
      expect(observed.postDataJSON().threadId).toBe(thread);
      await expect(view.locator('.scenario-transcript')).toContainText('E2E echo: After paced bootstrap');
      await expect(view.locator('.scenario-metrics')).toHaveAttribute('data-run', observed.postDataJSON().runId);
      await resetScenario(view);
      await view.getByRole('textbox', { name: 'Scenario message' }).fill('Fresh bootstrap canary');
      await view.getByRole('textbox', { name: 'Scenario message' }).press('Enter');
      await expect(view.locator('.scenario-transcript')).toContainText('E2E echo: Fresh bootstrap canary');
      await expect(view.locator('#blazor-error-ui')).not.toBeVisible();
    } finally {
      releaseBootstrap?.();
      await backend.deleteModel(model.id);
    }
  });
}
