import { test, expect, waitForInteractive, fluentButton, clickFluentButton } from '../fixtures';
import type { Page } from '@playwright/test';

async function send(page: Page, prompt: string) {
  await page.getByRole('textbox', { name: 'Scenario message' }).fill(prompt);
  await page.getByRole('textbox', { name: 'Scenario message' }).press('Enter');
}
test('Harness skills loads the bundled meeting skill over AG-UI', async ({ page, backend }) => {
  const model = await backend.createModel({ supportsTools: true });
  try {
    await page.goto(`/harness-skills?modelId=${model.id}`);
    await waitForInteractive(page);
    const script = await backend.createChatScript([
      { messageId: 'skill', tool: 'load_skill', arguments: { skillName: 'meeting-summary' } },
      { messageId: 'answer', text: 'Meeting notes summarized.' },
    ]);
    await send(page, script.prompt);
    await expect(page.locator('.scenario-transcript')).toContainText('Meeting notes summarized.');
    await expect(page.locator('.scenario-error')).toHaveCount(0);
  } finally { await backend.deleteModel(model.id); }
});

for (const scenario of [
  { route: 'harness-planning', text: 'Current todo list' },
  { route: 'harness-loop', text: '42' },
] as const) {
  test(`${scenario.route} executes through AG-UI`, async ({ page, backend }) => {
    const model = await backend.createModel({ supportsTools: true });
    try {
      await page.goto(`/${scenario.route}?modelId=${model.id}`);
      await waitForInteractive(page);
      const script = await backend.createChatScript([{ messageId: 'answer', text: scenario.text }]);
      await send(page, script.prompt);
      await expect(page.locator('.scenario-transcript')).toContainText(scenario.text);
      await expect(page.locator('.scenario-error')).toHaveCount(0);
    } finally { await backend.deleteModel(model.id); }
  });
}

test('Harness memory executes a temporary memory tool through AG-UI', async ({ page, backend }) => {
  const model = await backend.createModel({ supportsTools: true });
  try {
    await page.goto(`/harness-memory?modelId=${model.id}`);
    await waitForInteractive(page);
    const script = await backend.createChatScript([
      { messageId: 'memory', tool: 'file_memory_write', arguments: { fileName: 'note.md', content: 'orchids' } },
      { messageId: 'answer', text: 'Temporary note saved.' },
    ]);
    await send(page, script.prompt);
    await expect(page.locator('.scenario-transcript')).toContainText('Temporary note saved.');
    await expect(page.locator('.scenario-error')).toHaveCount(0);
  } finally { await backend.deleteModel(model.id); }
});

async function edit(page: Page, title: string, text: string) {
  await page.getByRole('textbox', { name: 'Document title', exact: true }).fill(title);
  await page.getByRole('textbox', { name: 'Document title', exact: true }).press('Tab');
  await page.getByRole('textbox', { name: 'Document text', exact: true }).fill(text);
  await page.getByRole('textbox', { name: 'Document text', exact: true }).press('Tab');
  await expect.poll(() => page.evaluate(() => {
    const item = sessionStorage.getItem(`agui-scenario:${location.pathname.slice(1)}`);
    return item ? JSON.parse(JSON.parse(item).content).state.document : null;
  })).toEqual({ title, text });
}

for (const path of ['happy', 'alternate', 'edge'] as const) {
  test(`Tools ${path}: typed catalogue results`, async ({ page, backend, fork }) => {
    const model = await backend.createModel({ supportsTools: true });
    try {
      await page.goto(`/tools?modelId=${model.id}`);
      await waitForInteractive(page);
      await fork.capture(page, 'tools-ready');
      const view = path === 'happy' ? page : await fork.open('tools-ready');
      if (view !== page) { await view.goto(`/tools?modelId=${model.id}`); await waitForInteractive(view); }
      const script = await backend.createChatScript([
        { messageId: 'tool', tool: path === 'happy' ? 'list_available_models' : 'get_model_details',
          arguments: path === 'happy' ? {} : { modelId: path === 'edge' ? 2147483647 : model.id } },
        { messageId: 'answer', text: 'Catalogue inspected.' },
      ]);
      await send(view, script.prompt);
      await expect(view.locator('.tool-block')).toContainText(path === 'edge' ? 'Model not found' : model.displayName);
      await expect(view.locator('.tool-block pre')).toHaveCount(0);
      await expect(view.locator('.scenario-transcript')).toContainText('Catalogue inspected.');
    } finally { await backend.deleteModel(model.id); }
  });

  test(`Approvals ${path}: real decisions and next-run selection`, async ({ page, backend, fork }) => {
    const model = await backend.createModel({ supportsTools: true });
    const target = await backend.createModel({ supportsTools: true });
    try {
      await page.goto(`/approvals?modelId=${model.id}`);
      await waitForInteractive(page);
      const script = await backend.createChatScript([
        { messageId: 'tool', tool: 'switch_active_model', arguments: { modelId: path === 'edge' ? 2147483647 : target.id } },
        { messageId: 'answer', text: 'Decision processed.' },
      ]);
      await send(page, script.prompt);
      await expect(page.locator('.approval-block')).toContainText('Approval required');
      await expect(page.locator('.scenario-pending')).toHaveCount(0);
      await fork.capture(page, 'proposal');
      const view = path === 'happy' ? page : await fork.open('proposal');
      if (view !== page) { await view.goto(`/approvals?modelId=${model.id}`); await waitForInteractive(view); }
      await clickFluentButton(fluentButton(view, path === 'alternate' ? 'Reject' : 'Approve'));
      if (path === 'edge') {
        await expect(view.locator('.scenario-error')).toContainText('fresh proposal');
        await expect(view.locator('.approval-block')).not.toContainText('Approved');
      } else {
        await expect(view.locator('.approval-block')).toContainText(path === 'happy' ? 'Approved' : 'Rejected');
        const nextRequest = view.waitForRequest(r => r.url().endsWith('/ag-ui/approval') && r.method() === 'POST');
        await send(view, 'Next model run');
        expect((await nextRequest).postDataJSON().state.modelId).toBe(path === 'happy' ? target.id : model.id);
        await expect(view.locator('.scenario-transcript')).toContainText('E2E echo: Next model run');
      }
      await expect(view.locator('.approval-block pre')).toHaveCount(0);
    } finally { await backend.deleteModel(model.id); await backend.deleteModel(target.id); }
  });

  test(`Shared state ${path}: editor and agent retain exact document data`, async ({ page, backend, fork }) => {
    const model = await backend.createModel({ supportsTools: true });
    try {
      await page.goto(`/shared-state?modelId=${model.id}`);
      await waitForInteractive(page);
      await edit(page, 'Original', 'Old text');
      await fork.capture(page, 'document');
      const view = path === 'happy' ? page : await fork.open('document');
      if (view !== page) { await view.goto(`/shared-state?modelId=${model.id}`); await waitForInteractive(view); }
      const replacement = path === 'alternate' ? '' : path === 'edge' ? ' ' : 'New text';
      const script = await backend.createChatScript([
        { messageId: 'tool', tool: 'update_document', arguments: { title: 'Revised', text: replacement } },
        { messageId: 'answer', text: 'Document updated.' },
      ]);
      const request = view.waitForRequest(r => r.url().endsWith('/ag-ui/shared-state') && r.method() === 'POST');
      await send(view, script.prompt);
      const input = (await request).postDataJSON();
      expect(input.state.document).toEqual({ title: 'Original', text: 'Old text' });
      await expect(view.getByRole('textbox', { name: 'Document title', exact: true })).toHaveValue('Revised');
      await expect(view.getByRole('textbox', { name: 'Document text', exact: true })).toHaveValue(replacement);
      await expect(view.locator('.scenario-transcript')).toContainText('Document updated.');
    } finally { await backend.deleteModel(model.id); }
  });

  test(`Predictive state ${path}: exact diff with accept, reject or stale original`, async ({ page, backend, fork }) => {
    const model = await backend.createModel({ supportsTools: true });
    try {
      await page.goto(`/predictive-state?modelId=${model.id}`);
      await waitForInteractive(page);
      await edit(page, 'Original', 'Old text');
      const script = await backend.createChatScript([
        { messageId: 'tool', tool: 'propose_document_edit',
          arguments: { title: 'Proposed', text: 'New text', originalTitle: 'Original', originalText: 'Old text' } },
        { messageId: 'answer', text: 'Review processed.' },
      ]);
      await send(page, script.prompt);
      await expect(page.locator('.document-diff .current-text')).toHaveText('Old text');
      await expect(page.locator('.document-diff .proposed-text')).toHaveText('New text');
      await expect(page.locator('.scenario-pending')).toHaveCount(0);
      await fork.capture(page, 'diff');
      const view = path === 'happy' ? page : await fork.open('diff');
      if (view !== page) { await view.goto(`/predictive-state?modelId=${model.id}`); await waitForInteractive(view); }
      if (path === 'edge') await edit(view, 'Changed', 'User changed text');
      await clickFluentButton(fluentButton(view, path === 'alternate' ? 'Reject' : 'Accept'));
      if (path === 'edge') {
        await expect(view.locator('.scenario-error')).toContainText('fresh proposal');
        await expect(view.getByRole('textbox', { name: 'Document text', exact: true })).toHaveValue('User changed text');
      } else {
        await expect(view.locator('.ui-action-block')).toContainText(path === 'happy' ? 'Accepted' : 'Rejected');
        await expect(view.getByRole('textbox', { name: 'Document text', exact: true }))
          .toHaveValue(path === 'happy' ? 'New text' : 'Old text');
      }
      await expect(view.locator('.ui-action-block pre')).toHaveCount(0);
    } finally { await backend.deleteModel(model.id); }
  });

  test(`Generative UI ${path}: real snapshot and incremental plan patch`, async ({ page, backend, fork }) => {
    const model = await backend.createModel({ supportsTools: true });
    try {
      await page.goto(`/generative-ui?modelId=${model.id}`);
      await waitForInteractive(page);
      const script = await backend.createChatScript([
        { messageId: 'create', tool: 'create_plan', arguments: { title: 'Launch plan', steps: ['Prepare', 'Deploy'] } },
        { messageId: 'update', tool: 'update_plan_step',
          arguments: { stepId: path === 'edge' ? 'missing' : 'step-1', status: path === 'alternate' ? 'blocked' : 'done' },
          waitForRelease: true },
        { messageId: 'answer', text: 'Plan processed.' },
      ]);
      await send(page, script.prompt);
      await expect(page.locator('.plan-checklist')).toContainText('Launch plan');
      await expect(page.locator('[data-step="step-1"]')).toContainText('pending');
      let view = page;
      if (path === 'happy') await backend.releaseChatScript(script.id);
      else {
        await expect.poll(() => page.evaluate(() => {
          const saved = sessionStorage.getItem('agui-scenario:generative-ui');
          return saved ? JSON.parse(JSON.parse(saved).content).state.plan?.title : null;
        })).toBe('Launch plan');
        await fork.capture(page, 'plan-snapshot');
        await clickFluentButton(fluentButton(page, 'Stop'));
        await expect.poll(async () => (await backend.chatScriptStatus(script.id)).cancelled).toBe(true);
        view = await fork.open('plan-snapshot');
        await view.goto(`/generative-ui?modelId=${model.id}`);
        await waitForInteractive(view);
        await expect(view.locator('.plan-checklist')).toContainText('Launch plan');
        const update = await backend.createChatScript([
          { messageId: 'fork-update', tool: 'update_plan_step',
            arguments: { stepId: path === 'edge' ? 'missing' : 'step-1', status: 'blocked' } },
          { messageId: 'fork-answer', text: 'Fork plan processed.' },
        ]);
        await send(view, update.prompt);
      }
      if (path === 'edge') {
        await expect(view.locator('.scenario-error')).toContainText('fresh proposal');
        await expect(view.locator('[data-step="step-1"]')).toContainText('pending');
      } else {
        await expect(view.locator('[data-step="step-1"]')).toContainText(path === 'alternate' ? 'blocked' : 'done');
      }
      await expect(view.locator('[data-step="step-2"]')).toContainText('pending');
      await expect(view.locator('.plan-checklist pre')).toHaveCount(0);
    } finally { await backend.deleteModel(model.id); }
  });
}

test('Reasoning: deterministic summary, collapse and subsequent live Chat remain functional', async ({ page, backend }) => {
  const model = await backend.createModel({ supportsTools: true, thinkingMode: 'Effort' });
  try {
    const script = await backend.createChatScript([
      { messageId: 'reason', reasoning: 'Comparing the catalogue capabilities.' },
      { messageId: 'answer', text: 'Reasoned answer.' },
    ]);
    await page.goto(`/reasoning?modelId=${model.id}`);
    await waitForInteractive(page);
    await send(page, script.prompt);
    await expect(page.locator('.reasoning-block')).toContainText('Reasoning');
    await clickFluentButton(page.locator('.reasoning-block fluent-button'));
    await expect(page.locator('.reasoning-block-content')).toHaveText('Comparing the catalogue capabilities.');
    await clickFluentButton(page.locator('.reasoning-block fluent-button'));
    await expect(page.locator('.reasoning-block-content')).toHaveCount(0);
    await page.goto('/chat');
    await waitForInteractive(page);
    await page.locator('.chat-input textarea').fill('Chat remains live');
    await page.locator('.chat-input textarea').press('Enter');
    await expect(page.locator('.assistant-message')).toContainText('E2E echo: Chat remains live');
  } finally { await backend.deleteModel(model.id); }
});
