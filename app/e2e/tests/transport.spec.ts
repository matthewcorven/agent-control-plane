import { test, expect, waitForInteractive, waitForWebAssembly, fluentButton, clickFluentButton, resetScenario } from '../fixtures';

test.describe('browser-owned scenario transport', () => {
  test('existing Chat also uses public-origin browser fetch in both actual renderers', async ({ page }) => {
    test.setTimeout(120000);
    await page.goto('/chat');
    await waitForInteractive(page);
    const origin = new URL(page.url()).origin;
    const initialRequest = page.waitForRequest(r => new URL(r.url()).pathname === '/ag-ui' && r.method() === 'POST', { timeout: 5000 });
    await page.locator('.chat-input textarea').fill('Legacy browser transport');
    await page.locator('.chat-input textarea').press('Enter');
    expect(new URL((await initialRequest).url()).origin).toBe(origin);
    await expect(page.locator('.chat-page')).toHaveAttribute('data-renderer', 'Server');
    await expect(page.locator('.assistant-message')).toContainText('E2E echo: Legacy browser transport');
    await waitForWebAssembly(page);
    await page.goto('/chat');
    await waitForInteractive(page);
    await expect(page.locator('.chat-page')).toHaveAttribute('data-browser', 'true');
    const wasmRequest = page.waitForRequest(r => new URL(r.url()).pathname === '/ag-ui' && r.method() === 'POST');
    await page.locator('.chat-input textarea').fill('Legacy WASM transport');
    await page.locator('.chat-input textarea').press('Enter');
    expect(new URL((await wasmRequest).url()).origin).toBe(origin);
    await expect(page.locator('.assistant-message')).toContainText('E2E echo: Legacy WASM transport');
  });

  test('Server browser fetch streams, then WASM resumes the same pending thread', async ({ page, backend }) => {
    test.setTimeout(120000);
    const model = await backend.createModel({ supportsTools: true, thinkingMode: 'Effort' });
    try {
      const script = await backend.createChatScript([
        { messageId: 'intro', text: 'Browser stream: 🧪 café ' + 'x'.repeat(40000) },
        { messageId: 'proposal', tool: 'switch_active_model', arguments: { modelId: model.id }, waitForRelease: true },
        { messageId: 'answer', text: 'Switch admitted.' },
      ]);
      await page.goto(`/approvals?modelId=${model.id}`);
      await waitForInteractive(page);
      await expect(page.locator('[data-renderer]')).toHaveAttribute('data-renderer', 'Server');
      const started = page.waitForRequest(r => r.url().endsWith('/ag-ui/approval') && r.method() === 'POST');
      await page.getByRole('textbox', { name: 'Scenario message' }).fill(script.prompt);
      await page.getByRole('textbox', { name: 'Scenario message' }).press('Enter');
      const initial = (await started).postDataJSON();
      await expect(page.locator('.scenario-transcript')).toContainText('Browser stream: 🧪 café');
      await expect(page.locator('.scenario-pending')).toBeVisible();
      await backend.releaseChatScript(script.id);
      await expect(page.locator('.approval-block')).toContainText('Approval required');
      const cookie = (await page.context().cookies()).find(c => c.name === 'agui-caller');
      expect(cookie).toMatchObject({ httpOnly: true, path: '/ag-ui', secure: false, sameSite: 'Lax' });
      expect(cookie?.domain).toBe(new URL(page.url()).hostname);
      await expect.poll(() => page.evaluate(() => Object.keys(sessionStorage)
        .some(k => k.startsWith('agui-scenario:')))).toBe(true);

      // Auto selects a new renderer on a subsequent document, not an in-place handoff.
      await waitForWebAssembly(page);
      await page.goto(`/approvals?modelId=${model.id}`);
      await waitForInteractive(page);
      await expect(page.locator('[data-renderer]')).toHaveAttribute('data-renderer', 'WebAssembly');
      await expect(page.locator('.approval-block')).toContainText('Approval required');
      const resumed = page.waitForRequest(r => r.url().endsWith('/ag-ui/approval') && r.method() === 'POST');
      await clickFluentButton(fluentButton(page, 'Approve'));
      const resume = (await resumed).postDataJSON();
      expect(resume.threadId).toBe(initial.threadId);
      expect(resume.runId).not.toBe(initial.runId);
      expect(resume.resume[0].payload).toMatchObject({
        approved: true, toolCall: { name: 'switch_active_model', arguments: { modelId: model.id } },
      });
      await expect(page.locator('.approval-block')).toContainText('Approved');
      await expect(page.locator('.scenario-transcript')).toContainText('Switch admitted.');
      await expect(page.locator('.scenario-metrics')).toHaveAttribute('data-run', resume.runId);
      expect((await backend.chatScriptStatus(script.id)).results).toHaveLength(1);
    } finally {
      await backend.deleteModel(model.id);
    }
  });

  test('Stop aborts upstream and preserves a live Server circuit', async ({ page, backend }) => {
    const model = await backend.createModel({ supportsTools: true });
    try {
      const script = await backend.createChatScript([
        { messageId: 'partial', text: 'Partial transport output.' },
        { messageId: 'tail', text: 'Must not arrive.', waitForRelease: true },
      ]);
      await page.goto(`/tools?modelId=${model.id}`);
      await waitForInteractive(page);
      await expect(page.locator('[data-renderer]')).toHaveAttribute('data-renderer', 'Server');
      await page.getByRole('textbox', { name: 'Scenario message' }).fill(script.prompt);
      await page.getByRole('textbox', { name: 'Scenario message' }).press('Enter');
      await expect(page.locator('.scenario-transcript')).toContainText('Partial transport output.');
      await clickFluentButton(fluentButton(page, 'Stop'));
      await expect.poll(async () => (await backend.chatScriptStatus(script.id)).cancelled).toBe(true);
      await resetScenario(page);
      await expect(page.locator('.scenario-transcript')).not.toContainText('Partial transport output.');
      await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
      await page.getByRole('textbox', { name: 'Scenario message' }).fill('Circuit canary');
      await page.getByRole('textbox', { name: 'Scenario message' }).press('Enter');
      await expect(page.locator('.scenario-transcript')).toContainText('E2E echo: Circuit canary');
    } finally {
      await backend.deleteModel(model.id);
    }
  });

  test('foreign browser cannot resume; concurrent owner forks execute once and replay recovers visibly', async ({ page, browser, baseURL, backend, fork }) => {
    const model = await backend.createModel({ supportsTools: true });
    const foreignContext = await browser.newContext({ baseURL });
    try {
      const script = await backend.createChatScript([
        { messageId: 'proposal', tool: 'switch_active_model', arguments: { modelId: model.id } },
        { messageId: 'answer', text: 'One admitted execution.' },
      ]);
      await page.goto(`/approvals?modelId=${model.id}`);
      await waitForInteractive(page);
      await page.getByRole('textbox', { name: 'Scenario message' }).fill(script.prompt);
      await page.getByRole('textbox', { name: 'Scenario message' }).press('Enter');
      await expect(page.locator('.approval-block')).toContainText('Approval required');
      await expect(page.locator('.scenario-pending')).toHaveCount(0);
      await fork.capture(page, 'pending-owner');
      const checkpoint = await page.evaluate(() => JSON.parse(JSON.parse(
        sessionStorage.getItem('agui-scenario:approval')!).content));
      const tool = checkpoint.tools[0];
      const payload = {
        threadId: checkpoint.threadId, runId: crypto.randomUUID(), state: checkpoint.state,
        messages: checkpoint.history, tools: [], context: [],
        resume: [{ interruptId: tool.interruptId, status: 'completed', payload: {
          approved: true, toolCall: { callId: tool.id, name: tool.name, arguments: JSON.parse(tool.arguments) },
        } }],
      };
      const foreign = await foreignContext.newPage();
      await foreign.goto('/');
      const rejected = await foreign.evaluate(async input => {
        const response = await fetch('/ag-ui/approval', {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input),
        });
        return { status: response.status, body: await response.json() };
      }, payload);
      expect(rejected.status).toBe(400);
      expect(rejected.body.code).toBe('approval_caller_missing');
      expect((await backend.chatScriptStatus(script.id)).results).toHaveLength(0);

      const peer = await fork.open('pending-owner');
      await peer.goto(`/approvals?modelId=${model.id}`);
      await waitForInteractive(peer);
      await Promise.all([
        clickFluentButton(fluentButton(page, 'Approve')),
        clickFluentButton(fluentButton(peer, 'Approve')),
      ]);
      await expect.poll(async () => (await backend.chatScriptStatus(script.id)).results.length).toBe(1);
      await expect.poll(async () => (await page.locator('.approval-block').textContent() ?? '') +
        (await peer.locator('.approval-block').textContent() ?? '')).toContain('Approved');
      const failed = await page.locator('.scenario-error').count() ? page : peer;
      await expect(failed.locator('.scenario-error')).toContainText('already consumed');
      await resetScenario(failed);
      await expect(failed.locator('.approval-block')).toHaveCount(0);
    } finally {
      await foreignContext.close();
      await backend.deleteModel(model.id);
    }
  });

  test('relay rejects foreign origin, missing origin, non-JSON and unlisted routes', async ({ request }) => {
    for (const headers of [
      { Origin: 'https://foreign.example', 'Content-Type': 'application/json' },
      { 'Content-Type': 'application/json' },
      { Origin: 'http://127.0.0.1:5299', 'Content-Type': 'text/plain' },
    ]) {
      const response = await request.post('/ag-ui/approval', { headers, data: '{}' });
      expect([403, 415]).toContain(response.status());
    }
    const unknown = await request.post('/ag-ui/not-an-agent', {
      headers: { Origin: 'http://127.0.0.1:5299', 'Content-Type': 'application/json' }, data: '{}',
    });
    expect(unknown.status()).toBe(404);
  });
});
