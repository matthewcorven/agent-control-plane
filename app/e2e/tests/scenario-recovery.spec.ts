import { test, expect, waitForInteractive, waitForWebAssembly, fluentButton, clickFluentButton, resetScenario } from '../fixtures';
import type { Page } from '@playwright/test';

async function send(page: Page, prompt: string) {
  await page.getByRole('textbox', { name: 'Scenario message' }).fill(prompt);
  await page.getByRole('textbox', { name: 'Scenario message' }).press('Enter');
}

async function draftThread(page: Page) {
  const shell = page.locator('.scenario-page');
  await expect(shell).toHaveAttribute('data-thread', /\S+/);
  const thread = await shell.getAttribute('data-thread');
  expect(thread).not.toBeNull();
  return thread!;
}

test('reset discards an unsent draft and leaves a live fresh conversation', async ({ page, backend }) => {
  const model = await backend.createModel({ supportsTools: true });
  try {
    await page.goto(`/tools?modelId=${model.id}`);
    await waitForInteractive(page);
    const oldThread = await draftThread(page);
    await page.getByRole('textbox', { name: 'Scenario message' }).fill('Unsent draft');
    await resetScenario(page);
    await expect(page.locator('.scenario-page')).not.toHaveAttribute('data-thread', oldThread!);
    await expect(page.getByRole('textbox', { name: 'Scenario message' })).toHaveValue('');
    await send(page, 'Fresh draft canary');
    await expect(page.locator('.scenario-transcript')).toContainText('E2E echo: Fresh draft canary');
    await expect(page.locator('.scenario-transcript')).not.toContainText('Unsent draft');
  } finally { await backend.deleteModel(model.id); }
});

test('draft thread capture waits for a held real scenario initialization', async ({ page, backend, fork }, testInfo) => {
  const model = await backend.createModel({ supportsTools: true });
  let releaseModule: (() => void) | undefined;
  try {
    await page.goto(`/tools?modelId=${model.id}`);
    await waitForInteractive(page);
    await send(page, 'Draft readiness predecessor');
    await expect(page.locator('.scenario-transcript')).toContainText('E2E echo: Draft readiness predecessor');
    await page.evaluate(() => sessionStorage.removeItem('agui-scenario:tools'));
    await fork.capture(page, 'draft-readiness');
    const view = await fork.open('draft-readiness', { cold: true });
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
    await expect.poll(() => downloaded).toBe(true);
    await waitForInteractive(view);
    const shell = view.locator('.scenario-page');
    await expect(shell).toHaveAttribute('data-renderer', 'Server');
    await expect(shell).toHaveAttribute('data-browser', 'false');
    await expect(shell).toContainText('Connecting to the agent...');
    expect(await shell.getAttribute('data-thread')).toBeNull();
    let settled = false;
    const capturing = draftThread(view).then(
      thread => { settled = true; return { thread, error: undefined }; },
      (error: unknown) => { settled = true; return { thread: null, error }; });
    try {
      await resetScenario(page);
      await send(page, 'Draft readiness release canary');
      await expect(page.locator('.scenario-transcript')).toContainText('E2E echo: Draft readiness release canary');
      expect(settled, 'Draft identity must wait for scenario initialization, not layout readiness').toBe(false);
    } finally {
      release();
    }
    const captured = await capturing;
    if (captured.error) throw captured.error;
    expect(captured.thread).not.toBeNull();
    await expect(shell).toHaveAttribute('data-thread', captured.thread!);
    await view.getByRole('textbox', { name: 'Scenario message' }).fill('Held unsent draft');
    await resetScenario(view);
    await expect(shell).not.toHaveAttribute('data-thread', captured.thread!);
    await expect(view.getByRole('textbox', { name: 'Scenario message' })).toHaveValue('');
    const request = view.waitForRequest(r => r.url().endsWith('/ag-ui/tools') && r.method() === 'POST');
    await send(view, 'Held draft fresh canary');
    const actual = await request;
    await expect(shell).toHaveAttribute('data-thread', actual.postDataJSON().threadId);
    expect(actual.postDataJSON().threadId).not.toBe(captured.thread);
    await expect(view.locator('.scenario-transcript')).toContainText('E2E echo: Held draft fresh canary');
    await expect(view.locator('.scenario-transcript')).not.toContainText('Held unsent draft');
    await expect(view.locator('#blazor-error-ui')).not.toBeVisible();
    await testInfo.attach('draft-thread-initialization', {
      contentType: 'application/json',
      body: JSON.stringify({ originalThread: captured.thread, freshThread: actual.postDataJSON().threadId }),
    });
  } finally {
    releaseModule?.();
    await backend.deleteModel(model.id);
  }
});

test('a truncated real execution stays visible as an error rather than successful completion', async ({ page, backend }) => {
  const model = await backend.createModel({ supportsTools: true });
  try {
    const script = await backend.createChatScript([{ messageId: 'partial', text: 'Real output before truncation' }]);
    await page.route('**/ag-ui/tools', async route => {
      const response = await route.fetch();
      const body = (await response.text()).split('\n').filter(line =>
        !line.startsWith('data: ') || JSON.parse(line.slice(6)).type !== 'RUN_FINISHED').join('\n');
      await route.fulfill({ response, body });
    });
    await page.goto(`/tools?modelId=${model.id}`);
    await waitForInteractive(page);
    await send(page, script.prompt);
    await expect(page.locator('.scenario-transcript')).toContainText('Real output before truncation');
    await expect(page.locator('.scenario-error')).toHaveAttribute('data-code', 'agent_stream_incomplete');
    expect((await backend.chatScriptStatus(script.id)).calls).toBe(1);
    await page.unroute('**/ag-ui/tools');
    await resetScenario(page);
    await send(page, 'Recovered stream canary');
    await expect(page.locator('.scenario-transcript')).toContainText('E2E echo: Recovered stream canary');
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
  } finally { await backend.deleteModel(model.id); }
});

test('large checkpoint restores through the Server bridge without replaying or duplicating history', async ({ page, backend, fork }) => {
  const model = await backend.createModel({ supportsTools: true });
  try {
    const answer = 'Saved café 🧪 ' + 'x'.repeat(40000);
    const script = await backend.createChatScript([{ messageId: 'large', text: answer }]);
    await page.goto(`/tools?modelId=${model.id}`);
    await waitForInteractive(page);
    await send(page, script.prompt);
    await expect(page.locator('.scenario-transcript')).toContainText(answer);
    await expect(page.locator('.scenario-pending')).toHaveCount(0);
    const originalThread = await page.locator('.scenario-page').getAttribute('data-thread');
    await fork.capture(page, 'large-server');
    const view = await fork.open('large-server', { cold: true });
    await view.goto(`/tools?modelId=${model.id}`);
    await waitForInteractive(view);
    await expect(view.locator('.scenario-page')).toHaveAttribute('data-renderer', 'Server');
    await expect(view.locator('.scenario-page')).toHaveAttribute('data-browser', 'false');
    await expect(view.locator('.scenario-transcript')).toContainText(answer);
    const request = view.waitForRequest(r => r.url().endsWith('/ag-ui/tools') && r.method() === 'POST');
    await send(view, 'Restored circuit canary');
    const input = (await request).postDataJSON();
    expect(input.threadId).toBe(originalThread);
    expect(input.messages.filter((m: { content?: string }) => m.content === answer)).toHaveLength(1);
    expect(input.messages.filter((m: { content?: string }) => m.content === script.prompt)).toHaveLength(1);
    await expect(view.locator('.scenario-transcript')).toContainText('E2E echo: Restored circuit canary');
    expect((await backend.chatScriptStatus(script.id)).calls).toBe(1);
    await expect(view.locator('#blazor-error-ui')).not.toBeVisible();
  } finally { await backend.deleteModel(model.id); }
});

test('reset during a live stream cancels upstream, rejects old callbacks and releases browser requests', async ({ page, backend }) => {
  const model = await backend.createModel({ supportsTools: true });
  try {
    const script = await backend.createChatScript([
      { messageId: 'old', text: 'Old partial response' },
      { messageId: 'late', text: 'Stale callback must not appear', waitForRelease: true },
    ]);
    await page.goto(`/tools?modelId=${model.id}`);
    await waitForInteractive(page);
    await send(page, script.prompt);
    await expect(page.locator('.scenario-transcript')).toContainText('Old partial response');
    const oldThread = await page.locator('.scenario-page').getAttribute('data-thread');
    await resetScenario(page);
    await expect.poll(async () => (await backend.chatScriptStatus(script.id)).cancelled).toBe(true);
    await backend.releaseChatScript(script.id);
    await expect(page.locator('.scenario-page')).not.toHaveAttribute('data-thread', oldThread!);
    await send(page, 'Fresh run');
    await expect(page.locator('.scenario-transcript')).toContainText('E2E echo: Fresh run');
    await expect(page.locator('.scenario-transcript')).not.toContainText('Stale callback');
    await page.goto('/models');
    await waitForInteractive(page);
    await expect.poll(() => page.evaluate(async () => {
      const bridge = await import('./agui-transport.js');
      return bridge.diagnostics();
    })).toEqual({ requests: 0, checkpoints: 0 });
  } finally { await backend.deleteModel(model.id); }
});

test('lost caller cookie produces a visible restart-required error without admitting approval', async ({ page, backend }) => {
  const model = await backend.createModel({ supportsTools: true });
  try {
    const script = await backend.createChatScript([
      { messageId: 'proposal', tool: 'switch_active_model', arguments: { modelId: model.id } },
      { messageId: 'answer', text: 'Must not execute' },
    ]);
    await page.goto(`/approvals?modelId=${model.id}`);
    await waitForInteractive(page);
    await send(page, script.prompt);
    await expect(page.locator('.approval-block')).toContainText('Approval required');
    await expect(page.locator('.scenario-pending')).toHaveCount(0);
    await page.context().clearCookies({ name: 'agui-caller' });
    await clickFluentButton(fluentButton(page, 'Approve'));
    await expect(page.locator('.scenario-error')).toHaveAttribute('data-code', 'approval_caller_missing');
    expect((await backend.chatScriptStatus(script.id)).results).toHaveLength(0);
    await expect.poll(() => page.evaluate(() => sessionStorage.getItem('agui-scenario:approval'))).toBeNull();
    await resetScenario(page);
    await expect(page.locator('.approval-block')).toHaveCount(0);
  } finally { await backend.deleteModel(model.id); }
});

for (const status of [400, 413]) {
  test(`real pre-SSE HTTP ${status} reaches visible recovery before any inference`, async ({ page, backend }) => {
    const model = await backend.createModel({ supportsTools: true });
    try {
      const script = await backend.createChatScript([{ messageId: 'never', text: 'Must not execute' }]);
      await page.route('**/ag-ui/approval', async route => {
        const input = route.request().postDataJSON();
        if (status === 400) input.threadId = '';
        else input.state.document.text = 'x'.repeat(70000);
        await route.continue({ postData: JSON.stringify(input) });
      });
      await page.goto(`/approvals?modelId=${model.id}`);
      await waitForInteractive(page);
      const response = page.waitForResponse(r => r.url().endsWith('/ag-ui/approval'));
      await send(page, script.prompt);
      expect((await response).status()).toBe(status);
      await expect(page.locator('.scenario-error')).toBeVisible();
      expect((await backend.chatScriptStatus(script.id)).calls).toBe(0);
      await expect(page.locator('.scenario-pending')).toHaveCount(0);
      await resetScenario(page);
      await expect(page.locator('.scenario-error')).toHaveCount(0);
    } finally { await backend.deleteModel(model.id); }
  });
}

test('corruption of a real state delta fails atomically after real tool execution', async ({ page, backend }) => {
  const model = await backend.createModel({ supportsTools: true });
  try {
    const script = await backend.createChatScript([
      { messageId: 'create', tool: 'create_plan', arguments: { title: 'Atomic plan', steps: ['First', 'Second'] } },
      { messageId: 'update', tool: 'update_plan_step', arguments: { stepId: 'step-1', status: 'done' } },
      { messageId: 'answer', text: 'Both real tools executed' },
    ]);
    await page.route('**/ag-ui/generative-ui', async route => {
      const response = await route.fetch();
      expect(response.status()).toBe(200);
      const original = await response.text();
      let corruptions = 0;
      const damaged = original.split('\n').map(line => {
        if (!line.startsWith('data: ')) return line;
        const event = JSON.parse(line.slice(6));
        if (event.type !== 'STATE_DELTA') return line;
        event.delta.push({ op: 'remove', path: '/does-not-exist' });
        corruptions++;
        return 'data: ' + JSON.stringify(event);
      }).join('\n');
      expect(corruptions).toBe(1);
      await route.fulfill({ response, body: damaged });
    });
    await page.goto(`/generative-ui?modelId=${model.id}`);
    await waitForInteractive(page);
    await send(page, script.prompt);
    await expect(page.locator('.scenario-error')).toBeVisible();
    await expect(page.locator('[data-step="step-1"]')).toContainText('pending');
    await expect(page.locator('[data-step="step-2"]')).toContainText('pending');
    expect((await backend.chatScriptStatus(script.id)).results).toHaveLength(2);
  } finally { await backend.deleteModel(model.id); }
});

for (const kind of ['expired', 'invalid'] as const) {
  test(`${kind} tab checkpoint clears authority and permits an explicit fresh conversation`, async ({ page, backend, fork }) => {
    const model = await backend.createModel({ supportsTools: true });
    try {
      await page.goto(`/shared-state?modelId=${model.id}`);
      await waitForInteractive(page);
      await expect(page.getByRole('textbox', { name: 'Scenario message' })).toBeVisible();
      const oldThread = await page.locator('.scenario-page').getAttribute('data-thread');
      await fork.capture(page, 'checkpoint');
      const view = await fork.open('checkpoint');
      await view.goto('/');
      await view.evaluate(value => {
        const key = 'agui-scenario:shared-state';
        if (value === 'invalid') sessionStorage.setItem(key, 'not-json');
        else {
          const item = JSON.parse(sessionStorage.getItem(key)!);
          item.savedAt = Date.now() - 31 * 60 * 1000;
          sessionStorage.setItem(key, JSON.stringify(item));
        }
      }, kind);
      await view.goto(`/shared-state?modelId=${model.id}`);
      await waitForInteractive(view);
      if (kind === 'invalid') {
        await expect(view.getByRole('alert')).toContainText('could not be restored');
        await resetScenario(view);
      }
      await expect(view.getByRole('textbox', { name: 'Scenario message' })).toBeVisible();
      await expect(view.locator('.scenario-page')).not.toHaveAttribute('data-thread', oldThread!);
      await expect(view.getByRole('textbox', { name: 'Document title', exact: true })).toHaveValue('Untitled');
    } finally { await backend.deleteModel(model.id); }
  });
}

test('reasoning alternate path streams into an expanded panel on a real WASM fork', async ({ page, backend, fork }) => {
  test.setTimeout(120000);
  const model = await backend.createModel({ thinkingMode: 'Effort', supportsTools: true });
  try {
    await page.goto(`/reasoning?modelId=${model.id}`);
    await waitForInteractive(page);
    await expect(page.getByRole('textbox', { name: 'Scenario message' })).toBeVisible();
    await fork.capture(page, 'reasoning-ready');
    const view = await fork.open('reasoning-ready');
    await waitForWebAssembly(view);
    await view.goto(`/reasoning?modelId=${model.id}`);
    await waitForInteractive(view);
    await expect(view.locator('.scenario-page')).toHaveAttribute('data-browser', 'true');
    const script = await backend.createChatScript([
      { messageId: 'reason', reasoning: 'First comparison. ' },
      { messageId: 'reason', reasoning: 'Second comparison.', waitForRelease: true },
      { messageId: 'answer', text: 'Reasoning complete' },
    ]);
    await send(view, script.prompt);
    await expect(view.locator('.reasoning-block')).toBeVisible();
    await clickFluentButton(view.locator('.reasoning-block fluent-button'));
    await expect(view.locator('.reasoning-block-content')).toHaveText('First comparison. ');
    await backend.releaseChatScript(script.id);
    await expect(view.locator('.reasoning-block-content')).toHaveText('First comparison. Second comparison.');
    await expect(view.locator('.reasoning-block-stats')).toContainText('2 chunks');
    await expect(view.locator('.scenario-transcript')).toContainText('Reasoning complete');
  } finally { await backend.deleteModel(model.id); }
});

test('reasoning edge rejects a non-reasoning model with a visible sanitized streaming error', async ({ page, backend, fork }) => {
  const model = await backend.createModel();
  try {
    await page.goto(`/reasoning?modelId=${model.id}`);
    await waitForInteractive(page);
    await expect(page.getByRole('textbox', { name: 'Scenario message' })).toBeVisible();
    await fork.capture(page, 'unsupported-reasoning');
    const view = await fork.open('unsupported-reasoning');
    await view.goto(`/reasoning?modelId=${model.id}`);
    await waitForInteractive(view);
    const script = await backend.createChatScript([{ messageId: 'never', text: 'Must not execute' }]);
    await send(view, script.prompt);
    await expect(view.locator('.scenario-error')).toHaveAttribute('data-code', 'StreamingError');
    await expect(view.locator('.scenario-error')).not.toContainText('Exception');
    expect((await backend.chatScriptStatus(script.id)).calls).toBe(0);
  } finally { await backend.deleteModel(model.id); }
});

test('reused provider tool IDs remain separate turns without duplicated resume messages', async ({ page, backend }) => {
  const model = await backend.createModel({ supportsTools: true });
  try {
    await page.goto(`/tools?modelId=${model.id}`);
    await waitForInteractive(page);
    for (const turn of [1, 2]) {
      const script = await backend.createChatScript([
        { messageId: `tool-${turn}`, tool: 'get_model_details', arguments: { modelId: model.id } },
        { messageId: `answer-${turn}`, text: `Turn ${turn} complete` },
      ]);
      await send(page, script.prompt);
      await expect(page.locator('.scenario-transcript')).toContainText(`Turn ${turn} complete`);
      await expect(page.locator('.tool-block')).toHaveCount(turn);
      expect((await backend.chatScriptStatus(script.id)).results).toHaveLength(1);
    }
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
  } finally { await backend.deleteModel(model.id); }
});

test('cold Server restores a pending approval and admits its exact browser-owned continuation once', async ({ page, backend, fork }) => {
  const model = await backend.createModel({ supportsTools: true });
  try {
    const script = await backend.createChatScript([
      { messageId: 'history', text: 'Cold pending history ' + 'x'.repeat(40000) },
      { messageId: 'proposal', tool: 'switch_active_model', arguments: { modelId: model.id } },
      { messageId: 'answer', text: 'Cold continuation admitted' },
    ]);
    await page.goto(`/approvals?modelId=${model.id}`);
    await waitForInteractive(page);
    await send(page, script.prompt);
    await expect(page.locator('.approval-block')).toContainText('Approval required');
    await expect(page.locator('.scenario-pending')).toHaveCount(0);
    const thread = await page.locator('.scenario-page').getAttribute('data-thread');
    await fork.capture(page, 'cold-approval');
    const view = await fork.open('cold-approval', { cold: true });
    await view.goto(`/approvals?modelId=${model.id}`);
    await waitForInteractive(view);
    await expect(view.locator('.scenario-page')).toHaveAttribute('data-renderer', 'Server');
    await expect(view.locator('.approval-block')).toContainText('Approval required');
    const request = view.waitForRequest(r => r.url().endsWith('/ag-ui/approval') && r.method() === 'POST');
    await clickFluentButton(fluentButton(view, 'Approve'));
    const input = (await request).postDataJSON();
    expect(input.threadId).toBe(thread);
    expect(input.resume[0].payload.toolCall.arguments).toEqual({ modelId: model.id });
    await expect(view.locator('.approval-block')).toContainText('Approved');
    await expect(view.locator('.scenario-transcript')).toContainText('Cold continuation admitted');
    await expect(view.locator('.scenario-metrics')).toHaveAttribute('data-run', input.runId);
    expect((await backend.chatScriptStatus(script.id)).results).toHaveLength(1);
    await expect(view.locator('#blazor-error-ui')).not.toBeVisible();
  } finally { await backend.deleteModel(model.id); }
});

test('WASM continuation preserves unknown root inference state and correlates real telemetry to the new run', async ({ page, backend, fork }) => {
  test.setTimeout(120000);
  const model = await backend.createModel({ supportsTools: true, thinkingMode: 'Effort' });
  try {
    await page.goto(`/tools?modelId=${model.id}`);
    await waitForInteractive(page);
    await expect(page.getByRole('textbox', { name: 'Scenario message' })).toBeVisible();
    await page.evaluate(() => {
      const key = 'agui-scenario:tools';
      const wrapper = JSON.parse(sessionStorage.getItem(key)!);
      const checkpoint = JSON.parse(wrapper.content);
      checkpoint.state.futureSetting = { retained: ['unknown', 7] };
      checkpoint.state.temperature = 0.31;
      checkpoint.state.thinkingEffort = 'Low';
      wrapper.content = JSON.stringify(checkpoint);
      sessionStorage.setItem(key, JSON.stringify(wrapper));
    });
    const thread = await page.locator('.scenario-page').getAttribute('data-thread');
    await fork.capture(page, 'unknown-state');
    const view = await fork.open('unknown-state');
    await waitForWebAssembly(view);
    await view.goto(`/tools?modelId=${model.id}`);
    await waitForInteractive(view);
    await expect(view.locator('.scenario-page')).toHaveAttribute('data-renderer', 'WebAssembly');
    const request = view.waitForRequest(r => r.url().endsWith('/ag-ui/tools') && r.method() === 'POST');
    await send(view, 'Preserve conversation inference');
    const input = (await request).postDataJSON();
    expect(input.threadId).toBe(thread);
    expect(input.state.futureSetting).toEqual({ retained: ['unknown', 7] });
    expect(input.state.temperature).toBe(0.31);
    expect(input.state.thinkingEffort).toBe('Low');
    await expect(view.locator('.scenario-transcript')).toContainText('E2E echo: Preserve conversation inference');
    await expect(view.locator('.scenario-metrics')).toHaveAttribute('data-run', input.runId);
    await clickFluentButton(view.locator('.monitoring-container fluent-button'));
    await expect(view.locator('.monitoring-content')).toContainText(model.modelId);
  } finally { await backend.deleteModel(model.id); }
});

test('enhanced navigation disposes the running owner and prevents stale callbacks on a new view', async ({ page, backend }) => {
  const model = await backend.createModel({ supportsTools: true });
  try {
    const script = await backend.createChatScript([
      { messageId: 'old', text: 'Navigation partial' },
      { messageId: 'late', text: 'Navigation stale output', waitForRelease: true },
    ]);
    await page.goto(`/tools?modelId=${model.id}`);
    await waitForInteractive(page);
    await send(page, script.prompt);
    await expect(page.locator('.scenario-transcript')).toContainText('Navigation partial');
    const thread = await page.locator('.scenario-page').getAttribute('data-thread');
    expect(thread).not.toBeNull();
    await page.getByRole('link', { name: 'Models', exact: true }).click();
    await waitForInteractive(page);
    await expect.poll(async () => (await backend.chatScriptStatus(script.id)).cancelled).toBe(true);
    await expect.poll(() => page.evaluate(async () =>
      (await import('./agui-transport.js')).diagnostics())).toEqual({ requests: 0, checkpoints: 0 });
    await backend.releaseChatScript(script.id);
    await page.getByRole('link', { name: 'Tools', exact: true }).click();
    await waitForInteractive(page);
    await expect(page.locator('.scenario-page')).toHaveAttribute('data-thread', thread!);
    await resetScenario(page);
    const canary = page.waitForRequest(r => r.url().endsWith('/ag-ui/tools') && r.method() === 'POST');
    await send(page, 'Navigation circuit canary');
    const request = await canary;
    expect(request.postDataJSON().threadId).not.toBe(thread);
    await expect(page.locator('.scenario-page')).toHaveAttribute('data-thread', request.postDataJSON().threadId);
    await expect(page.locator('.scenario-transcript')).toContainText('E2E echo: Navigation circuit canary');
    await expect(page.locator('.scenario-transcript')).not.toContainText('Navigation stale output');
    await expect(page.locator('.scenario-metrics')).toHaveAttribute('data-run', request.postDataJSON().runId);
  } finally { await backend.deleteModel(model.id); }
});

test('malformed typed result from a real execution is visible and does not kill the Server circuit', async ({ page, backend }) => {
  const model = await backend.createModel({ supportsTools: true });
  try {
    const script = await backend.createChatScript([
      { messageId: 'catalogue', tool: 'list_available_models', arguments: {} },
      { messageId: 'answer', text: 'Real catalogue executed' },
    ]);
    await page.route('**/ag-ui/tools', async route => {
      const response = await route.fetch();
      const original = await response.text();
      let damaged = 0;
      const body = original.split('\n').map(line => {
        if (!line.startsWith('data: ')) return line;
        const event = JSON.parse(line.slice(6));
        if (event.type !== 'TOOL_CALL_RESULT') return line;
        event.content = '{}';
        damaged++;
        return 'data: ' + JSON.stringify(event);
      }).join('\n');
      expect(damaged).toBe(1);
      await route.fulfill({ response, body });
    });
    await page.goto(`/tools?modelId=${model.id}`);
    await waitForInteractive(page);
    await expect(page.locator('.scenario-page')).toHaveAttribute('data-renderer', 'Server');
    await send(page, script.prompt);
    await expect(page.locator('.scenario-error')).toBeVisible();
    await expect(page.locator('.tool-block')).toContainText('Failed');
    expect((await backend.chatScriptStatus(script.id)).results).toHaveLength(1);
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
    await page.unroute('**/ag-ui/tools');
    await resetScenario(page);
    await send(page, 'Typed result recovery canary');
    await expect(page.locator('.scenario-transcript')).toContainText('E2E echo: Typed result recovery canary');
  } finally { await backend.deleteModel(model.id); }
});
