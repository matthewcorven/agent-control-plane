import { test, expect, waitForInteractive, waitForWebAssembly, resetScenario } from '../fixtures';

for (const path of ['cold-restore', 'cold-fresh', 'wasm-restore'] as const) {
  test(`reset waits for real scenario initialization on ${path}`, async ({ page, backend, fork }, testInfo) => {
    test.setTimeout(120000);
    const model = await backend.createModel({ supportsTools: true });
    let releaseModule: (() => void) | undefined;
    try {
      if (path === 'wasm-restore') await waitForWebAssembly(page);
      await page.goto(`/tools?modelId=${model.id}`);
      await waitForInteractive(page);
      const input = page.getByRole('textbox', { name: 'Scenario message' });
      await input.fill('Readiness predecessor');
      await input.press('Enter');
      await expect(page.locator('.scenario-transcript')).toContainText('E2E echo: Readiness predecessor');
      const predecessorThread = await page.locator('.scenario-page').getAttribute('data-thread');
      expect(predecessorThread).not.toBeNull();
      if (path === 'cold-fresh')
        await page.evaluate(() => sessionStorage.removeItem('agui-scenario:tools'));
      await fork.capture(page, 'readiness');
      const view = await fork.open('readiness', { cold: path !== 'wasm-restore' });
      let release!: () => void;
      const released = new Promise<void>(resolve => { release = resolve; });
      releaseModule = release;
      let downloaded = false;
      await view.route(/\/agui-transport(?:\.[^/]+)?\.js$/, async route => {
        const response = await route.fetch();
        expect(response.status()).toBe(200);
        downloaded = true;
        await released;
        await route.fulfill({ response });
      });
      await view.goto(`/tools?modelId=${model.id}`);
      await expect.poll(() => downloaded, { message: 'Hold the fetched real transport module' }).toBe(true);
      await waitForInteractive(view);
      const shell = view.locator('.scenario-page');
      await expect(shell).toHaveAttribute('data-renderer', path === 'wasm-restore' ? 'WebAssembly' : 'Server');
      await expect(shell).toHaveAttribute('data-browser', path === 'wasm-restore' ? 'true' : 'false');
      await expect(shell).toContainText('Connecting to the agent...');
      expect(await shell.getAttribute('data-thread')).toBeNull();
      await view.evaluate(() => {
        const shell = document.querySelector('.scenario-page')!;
        const threads: string[] = [];
        const observer = new MutationObserver(() => {
          const thread = shell.getAttribute('data-thread');
          if (thread && threads.at(-1) !== thread) threads.push(thread);
        });
        observer.observe(shell, { attributes: true, attributeFilter: ['data-thread'] });
        Object.assign(window, { readinessThreadObservation: { threads, observer } });
      });
      let settled = false;
      const resetting = resetScenario(view).then(
        () => { settled = true; return { error: undefined }; },
        (error: unknown) => { settled = true; return { error }; });
      try {
        await resetScenario(page);
        await input.fill('Readiness release canary');
        await input.press('Enter');
        await expect(page.locator('.scenario-transcript')).toContainText('E2E echo: Readiness release canary');
        expect(settled, 'Reset must wait for the actual scenario thread, not layout readiness').toBe(false);
      } finally {
        release();
        const result = await resetting;
        if (result.error) throw result.error;
      }
      const threads = await view.evaluate(() => {
        const observation = (window as Window & {
          readinessThreadObservation: { threads: string[]; observer: MutationObserver };
        }).readinessThreadObservation;
        observation.observer.disconnect();
        return observation.threads;
      });
      expect(threads).toHaveLength(2);
      if (path !== 'cold-fresh') expect(threads[0]).toBe(predecessorThread);
      else expect(threads[0]).not.toBe(predecessorThread);
      expect(threads[1]).not.toBe(threads[0]);
      await expect(shell).toHaveAttribute('data-thread', threads[1]);
      await expect(shell).toHaveAttribute('data-resetting', 'false');
      await expect(view.locator('.scenario-transcript')).not.toContainText('Readiness predecessor');
      const request = view.waitForRequest(r => r.url().endsWith('/ag-ui/tools') && r.method() === 'POST');
      await view.getByRole('textbox', { name: 'Scenario message' }).fill('Readiness fresh canary');
      await view.getByRole('textbox', { name: 'Scenario message' }).press('Enter');
      const actual = await request;
      expect(new URL(actual.url()).origin).toBe(new URL(view.url()).origin);
      expect(actual.postDataJSON().threadId).toBe(threads[1]);
      await expect(view.locator('.scenario-transcript')).toContainText('E2E echo: Readiness fresh canary');
      await expect(view.locator('.scenario-metrics')).toHaveAttribute('data-run', actual.postDataJSON().runId);
      await expect.poll(() => view.evaluate(async () =>
        (await import('./agui-transport.js')).diagnostics())).toEqual({ requests: 0, checkpoints: 0 });
      await expect(view.locator('#blazor-error-ui')).not.toBeVisible();
      await testInfo.attach('real-thread-publication', {
        contentType: 'application/json',
        body: JSON.stringify({ path, predecessorThread, threads, runId: actual.postDataJSON().runId }),
      });
    } finally {
      releaseModule?.();
      await backend.deleteModel(model.id);
    }
  });
}
