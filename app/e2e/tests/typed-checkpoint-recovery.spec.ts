import { test, expect, waitForInteractive, waitForWebAssembly, resetScenario, fluentButton, clickFluentButton } from '../fixtures';
import type { Backend, StateFork } from '../fixtures';
import type { Page, TestInfo } from '@playwright/test';

async function send(page: Page, prompt: string) {
  const input = page.getByRole('textbox', { name: 'Scenario message' });
  await input.fill(prompt);
  await input.press('Enter');
}

const scenarios = [
  { name: 'approval', path: 'approvals', tool: 'switch_active_model', block: '.approval-block',
    arguments: (id: number): Record<string, unknown> => ({ modelId: id }) },
  { name: 'predictive-state', path: 'predictive-state', tool: 'propose_document_edit', block: '.ui-action-block',
    arguments: (): Record<string, unknown> => ({ title: 'Proposed', text: 'New text', originalTitle: 'Untitled', originalText: '' }) },
] as const;

async function openRenderer(fork: StateFork, name: string, renderer: string) {
  const view = await fork.open(name, { cold: renderer === 'Server' });
  if (renderer === 'WebAssembly') {
    await waitForWebAssembly(view);
  }
  return view;
}

async function assertRenderer(view: Page, renderer: string) {
  await expect(view.locator('.scenario-page')).toHaveAttribute('data-renderer', renderer);
  await expect(view.locator('.scenario-page')).toHaveAttribute('data-browser', String(renderer === 'WebAssembly'));
}

async function evidence(view: Page, backend: Backend, scriptId: string, testInfo: TestInfo) {
  await testInfo.attach('checkpoint-circuit-evidence', {
    contentType: 'application/json',
    body: JSON.stringify({
      renderer: await view.evaluate(() => document.querySelector('.scenario-page')?.getAttribute('data-renderer')),
      blazorErrorVisible: await view.locator('#blazor-error-ui').isVisible(),
      alerts: await view.getByRole('alert').allTextContents(),
      backend: await backend.chatScriptStatus(scriptId),
      callerCookies: (await view.context().cookies()).filter(cookie => cookie.name === 'agui-caller')
        .map(({ value, ...attributes }) => attributes),
    }, null, 2),
  });
}

for (const renderer of ['Server', 'WebAssembly']) {
  for (const scenario of scenarios) {
    const corruptions: { name: string; field?: string; value?: unknown; remove?: boolean }[] = scenario.name === 'approval' ? [
      { name: 'empty arguments' },
      { name: 'string modelId', field: 'modelId', value: '1' },
      { name: 'zero modelId', field: 'modelId', value: 0 },
      { name: 'fractional modelId', field: 'modelId', value: 1.5 },
      { name: 'out-of-range modelId', field: 'modelId', value: 2147483648 },
    ] : [
      ...['title', 'text', 'originalTitle', 'originalText'].map(field => ({ name: `missing ${field}`, field, remove: true })),
      { name: 'non-string title', field: 'title', value: 7 },
      { name: 'blank title', field: 'title', value: ' \t' },
      { name: 'null text', field: 'text', value: null },
      { name: 'non-string originalTitle', field: 'originalTitle', value: false },
      { name: 'non-string originalText', field: 'originalText', value: [] },
    ];
    for (const corruption of corruptions) {
      test(`semantic pending ${scenario.name} checkpoint: ${renderer} rejects ${corruption.name} without losing its circuit`, async ({ page, backend, fork }, testInfo) => {
        test.setTimeout(120000);
        const model = await backend.createModel({ supportsTools: true });
        let view: Page | undefined;
        const script = await backend.createChatScript([
          { messageId: 'proposal', tool: scenario.tool, arguments: scenario.arguments(model.id) },
          { messageId: 'answer', text: 'Must not execute corrupted proposal' },
        ]);
        try {
          await page.goto(`/${scenario.path}?modelId=${model.id}`);
          await waitForInteractive(page);
          await send(page, script.prompt);
          await expect(page.locator(scenario.block)).toContainText('Approval required');
          await expect(page.locator('.scenario-pending')).toHaveCount(0);
          await page.evaluate(({ route, corruption }) => {
            const key = `agui-scenario:${route}`;
            const wrapper = JSON.parse(sessionStorage.getItem(key)!);
            const checkpoint = JSON.parse(wrapper.content);
            if (checkpoint.tools.length !== 1 || !checkpoint.tools[0].interruptId)
              throw new Error('Expected exactly one real pending proposal');
            const argumentsValue = JSON.parse(checkpoint.tools[0].arguments);
            if (!corruption.field) checkpoint.tools[0].arguments = '{}';
            else {
              if (corruption.remove) delete argumentsValue[corruption.field];
              else argumentsValue[corruption.field] = corruption.value;
              checkpoint.tools[0].arguments = JSON.stringify(argumentsValue);
            }
            wrapper.content = JSON.stringify(checkpoint);
            sessionStorage.setItem(key, JSON.stringify(wrapper));
          }, { route: scenario.name, corruption });
          await fork.capture(page, 'malformed');
          view = await openRenderer(fork, 'malformed', renderer);
          let restoreRequests = 0;
          view.on('request', request => { if (request.url().endsWith(`/ag-ui/${scenario.name}`)) restoreRequests++; });
          await view.goto(`/${scenario.path}?modelId=${model.id}`);
          await waitForInteractive(view);
          await assertRenderer(view, renderer);
          await expect(view.getByRole('alert')).toContainText('could not be restored');
          await expect(view.locator('#blazor-error-ui')).not.toBeVisible();
          expect(restoreRequests).toBe(0);
          expect(await backend.chatScriptStatus(script.id)).toEqual({ calls: 1, cancelled: false, results: [] });
          await expect.poll(() => view!.evaluate(route => sessionStorage.getItem(`agui-scenario:${route}`), scenario.name)).toBeNull();
          await resetScenario(view);
          await expect(view.locator(scenario.block)).toHaveCount(0);
          await send(view, 'Semantic checkpoint recovery canary');
          await expect(view.locator('.scenario-transcript')).toContainText('E2E echo: Semantic checkpoint recovery canary');
          await assertRenderer(view, renderer);
          await expect(view.locator('#blazor-error-ui')).not.toBeVisible();
          expect(restoreRequests).toBe(1);
          expect((await backend.chatScriptStatus(script.id)).results).toHaveLength(0);
        } finally {
          if (view) await evidence(view, backend, script.id, testInfo);
          await backend.deleteModel(model.id);
        }
      });
    }

    for (const documentText of scenario.name === 'approval' ? [''] : ['', ' \t\n']) {
    test(`valid pending ${scenario.name} checkpoint: ${renderer} preserves opaque arguments and exact ${documentText === '' ? 'empty' : 'whitespace'} document fields`, async ({ page, backend, fork }, testInfo) => {
      test.setTimeout(120000);
      const model = await backend.createModel({ supportsTools: true });
      let view: Page | undefined;
      const argumentsValue = scenario.name === 'approval' ?
        { ...scenario.arguments(model.id), future: { opaque: ['unchanged', 7] } } :
        { title: ' Proposed ', text: documentText, originalTitle: '', originalText: documentText };
      const script = await backend.createChatScript([
        { messageId: 'proposal', tool: scenario.tool, arguments: argumentsValue },
        { messageId: 'answer', text: 'Valid exact proposal completed' },
      ]);
      try {
        await page.goto(`/${scenario.path}?modelId=${model.id}`);
        await waitForInteractive(page);
        if (scenario.name === 'predictive-state') {
          await page.getByRole('textbox', { name: 'Document title', exact: true }).fill('');
          await page.getByRole('textbox', { name: 'Document title', exact: true }).press('Tab');
          await page.getByRole('textbox', { name: 'Document text', exact: true }).fill(documentText);
          await page.getByRole('textbox', { name: 'Document text', exact: true }).press('Tab');
          await expect.poll(() => page.evaluate(() =>
            JSON.parse(JSON.parse(sessionStorage.getItem('agui-scenario:predictive-state')!).content).state.document))
            .toEqual({ title: '', text: documentText });
        }
        await send(page, script.prompt);
        await expect(page.locator(scenario.block)).toContainText('Approval required');
        await expect(page.locator('.scenario-pending')).toHaveCount(0);
        const thread = await page.locator('.scenario-page').getAttribute('data-thread');
        await page.evaluate(route => {
          const key = `agui-scenario:${route}`;
          const wrapper = JSON.parse(sessionStorage.getItem(key)!);
          const checkpoint = JSON.parse(wrapper.content);
          checkpoint.state.futureSetting = { retained: ['unknown', 7] };
          checkpoint.state.temperature = 0.31;
          wrapper.content = JSON.stringify(checkpoint);
          sessionStorage.setItem(key, JSON.stringify(wrapper));
        }, scenario.name);
        await fork.capture(page, 'valid-pending');
        view = await openRenderer(fork, 'valid-pending', renderer);
        await view.goto(`/${scenario.path}?modelId=${model.id}`);
        await waitForInteractive(view);
        await assertRenderer(view, renderer);
        await expect(view.locator(scenario.block)).toContainText('Approval required');
        const resume = view.waitForRequest(request => request.url().endsWith(`/ag-ui/${scenario.name}`) && request.method() === 'POST');
        await clickFluentButton(fluentButton(view, scenario.name === 'approval' ? 'Approve' : 'Accept'));
        const input = (await resume).postDataJSON();
        expect(input.threadId).toBe(thread);
        expect(input.resume[0].payload.toolCall.arguments).toEqual(argumentsValue);
        expect(input.resume[0].payload.approved).toBe(true);
        expect(input.state.futureSetting).toEqual({ retained: ['unknown', 7] });
        expect(input.state.temperature).toBe(0.31);
        await expect(view.locator('.scenario-transcript')).toContainText('Valid exact proposal completed');
        expect((await backend.chatScriptStatus(script.id)).results).toHaveLength(1);
        if (scenario.name === 'predictive-state') {
          await expect(view.getByRole('textbox', { name: 'Document title', exact: true })).toHaveValue(' Proposed ');
          await expect(view.getByRole('textbox', { name: 'Document text', exact: true })).toHaveValue(documentText);
        }
        await expect(view.locator('#blazor-error-ui')).not.toBeVisible();
      } finally {
        if (view) await evidence(view, backend, script.id, testInfo);
        await backend.deleteModel(model.id);
      }
    });
    }

    for (const fault of ['missing-end', 'semantic'] as const) {
    test(`malformed observed ${scenario.name} arguments: ${renderer} cannot publish an unchecked interrupt (${fault})`, async ({ page, backend, fork }) => {
      test.setTimeout(120000);
      const model = await backend.createModel({ supportsTools: true });
      try {
        await page.goto(`/${scenario.path}?modelId=${model.id}`);
        await waitForInteractive(page);
        await expect(page.getByRole('textbox', { name: 'Scenario message' })).toBeVisible();
        await fork.capture(page, 'live-ready');
        const view = await openRenderer(fork, 'live-ready', renderer);
        await view.goto(`/${scenario.path}?modelId=${model.id}`);
        await waitForInteractive(view);
        await assertRenderer(view, renderer);
        const script = await backend.createChatScript([
          { messageId: 'proposal', tool: scenario.tool, arguments: scenario.arguments(model.id) },
          { messageId: 'answer', text: 'Must not execute corrupted live proposal' },
        ]);
        await view.route(`**/ag-ui/${scenario.name}`, async route => {
          const response = await route.fetch();
          expect(response.status()).toBe(200);
          let corruptions = 0;
          let removedEnds = 0;
          const body = (await response.text()).split('\n').map(line => {
            if (!line.startsWith('data: ')) return line;
            const event = JSON.parse(line.slice(6));
            if (event.type === 'TOOL_CALL_ARGS') {
              const damagedArguments = scenario.name === 'approval' ? { modelId: '1' } :
                { title: 'Proposed', text: 'New text', originalTitle: 'Untitled' };
              event.delta = corruptions++ === 0 ? (fault === 'missing-end' ? '{}' : JSON.stringify(damagedArguments)) : '';
              return 'data: ' + JSON.stringify(event);
            }
            if (event.type === 'TOOL_CALL_END' && fault === 'missing-end') { removedEnds++; return ''; }
            return line;
          }).join('\n');
          expect(corruptions).toBeGreaterThan(0);
          expect(removedEnds).toBe(fault === 'missing-end' ? 1 : 0);
          await route.fulfill({ response, body });
        });
        await send(view, script.prompt);
        await expect(view.locator('.scenario-error')).toBeVisible();
        await expect(view.locator(scenario.block)).toHaveCount(0);
        await expect(view.locator('#blazor-error-ui')).not.toBeVisible();
        expect(await backend.chatScriptStatus(script.id)).toEqual({ calls: 1, cancelled: false, results: [] });
        await expect.poll(() => view.evaluate(route => sessionStorage.getItem(`agui-scenario:${route}`), scenario.name)).toBeNull();
        await view.unroute(`**/ag-ui/${scenario.name}`);
        await resetScenario(view);
        await send(view, 'Observed projection recovery canary');
        await expect(view.locator('.scenario-transcript')).toContainText('E2E echo: Observed projection recovery canary');
        await expect(view.locator('#blazor-error-ui')).not.toBeVisible();
      } finally { await backend.deleteModel(model.id); }
    });
    }
  }
}
