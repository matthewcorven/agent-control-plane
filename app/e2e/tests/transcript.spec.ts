import { test, expect, waitForInteractive, clickFluentButton, fluentButton } from '../fixtures';

const PARTIAL = '## Partial answer\n\nSaved **partial** text.';

test.describe('rich transcript completion and message identity', () => {
  test.beforeEach(async ({ page, fork }) => {
    await page.goto('/chat');
    await waitForInteractive(page);
    await fork.capture(page, 'ready-chat');
  });

  test('happy path: a cumulative streamed block finishes once without a status note', async ({ page, backend }) => {
    const script = await backend.createChatScript([
      { messageId: 'answer', text: PARTIAL },
      { messageId: 'answer', text: '\n\nCompleted tail.', waitForRelease: true },
    ]);
    await page.locator('.chat-input textarea').fill(script.prompt);
    await page.locator('.chat-input textarea').press('Enter');
    const assistant = page.locator('.assistant-message');
    await expect(assistant.locator('.rt-h2')).toHaveText('Partial answer');
    await expect(assistant.locator('.streaming-cursor')).toHaveCount(1);
    await backend.releaseChatScript(script.id);

    await expect(assistant.locator('.streaming-cursor')).toHaveCount(0);
    await expect(assistant.locator('.rt-h2')).toHaveCount(1);
    await expect(assistant.locator('p.rt-p')).toHaveText(['Saved partial text.', 'Completed tail.']);
    await expect(assistant.locator('.message-status')).toHaveCount(0);
  });

  test('alternate path: Stop retains rich partial text and a visible stopped status', async ({ backend, fork }) => {
    const page = await fork.open('ready-chat');
    await page.goto('/chat');
    await waitForInteractive(page);
    const script = await backend.createChatScript([
      { messageId: 'answer', text: PARTIAL },
      { messageId: 'answer', waitForRelease: true },
    ]);
    await page.locator('.chat-input textarea').fill(script.prompt);
    await page.locator('.chat-input textarea').press('Enter');
    const assistant = page.locator('.assistant-message').first();
    await expect(assistant.locator('.rt-h2')).toHaveText('Partial answer');
    await clickFluentButton(fluentButton(page, 'Stop'));

    await expect(assistant.locator('.message-status')).toHaveText('Response stopped.');
    await expect(assistant.locator('p.rt-p')).toHaveText('Saved partial text.');
    await expect(assistant.locator('.rt-h2')).toHaveCount(1);
    await expect(assistant.locator('.streaming-cursor')).toHaveCount(0);
    await expect(page.locator('.chat-input textarea')).toBeEnabled();

    await page.locator('.chat-input textarea').fill('after stop');
    await page.locator('.chat-input textarea').press('Enter');
    await expect(page.locator('.assistant-message').last()).toContainText('E2E echo: after stop');
    await expect(assistant).toContainText('Response stopped.');
  });

  test('edge case: an upstream error retains partial nodes and a visible error status', async ({ backend, fork }) => {
    const page = await fork.open('ready-chat');
    await page.goto('/chat');
    await waitForInteractive(page);
    const script = await backend.createChatScript([
      { messageId: 'answer', text: PARTIAL },
      { messageId: 'answer', error: 'E2E upstream failure', waitForRelease: true },
    ]);
    await page.locator('.chat-input textarea').fill(script.prompt);
    await page.locator('.chat-input textarea').press('Enter');
    const assistant = page.locator('.assistant-message').first();
    await expect(assistant.locator('.rt-h2')).toHaveText('Partial answer');
    await backend.releaseChatScript(script.id);

    await expect(assistant.locator('.message-status')).toHaveCount(1);
    await expect(assistant.locator('.message-status')).toHaveText(
      'Error: The response could not be completed. Please try again.');
    await expect(assistant).not.toContainText('E2E upstream failure');
    await expect(assistant.locator('p.rt-p')).toHaveText('Saved partial text.');
    await expect(assistant.locator('.rt-h2')).toHaveCount(1);
    await expect(assistant.locator('.streaming-cursor')).toHaveCount(0);
    await expect(page.locator('.chat-input textarea')).toBeEnabled();

    await clickFluentButton(fluentButton(page, 'New chat'));
    await expect(page.locator('.assistant-message')).toHaveCount(0);
    await page.locator('.chat-input textarea').fill('after error reset');
    await page.locator('.chat-input textarea').press('Enter');
    await expect(page.locator('.assistant-message')).toContainText('E2E echo: after error reset');
  });

  test('alternate path: distinct assistant message IDs remain ordered through later updates', async ({ backend, fork }) => {
    const page = await fork.open('ready-chat');
    await page.goto('/chat');
    await waitForInteractive(page);
    const script = await backend.createChatScript([
      { messageId: 'first', text: '## First answer\n\nFirst' },
      { messageId: 'first', text: ' finalized.\n\n', waitForRelease: true },
      { messageId: 'second', text: '## Second answer\n\nSecond' },
      { messageId: 'second', text: ' finalized.', waitForRelease: true },
    ]);
    await page.locator('.chat-input textarea').fill(script.prompt);
    await page.locator('.chat-input textarea').press('Enter');
    const assistant = page.locator('.assistant-message');
    await expect(assistant.locator('.rt-h2')).toHaveText('First answer');
    await backend.releaseChatScript(script.id);
    await expect(assistant.locator('.rt-h2').last()).toHaveText('Second answer');
    await expect(assistant.locator('.rt-h2')).toHaveText(['First answer', 'Second answer']);
    await backend.releaseChatScript(script.id);

    await expect(assistant.locator('.streaming-cursor')).toHaveCount(0);
    await expect(assistant.locator('.rt-h2')).toHaveText(['First answer', 'Second answer']);
    await expect(assistant.locator('p.rt-p')).toHaveText(['First finalized.', 'Second finalized.']);
  });

  test('edge case: identical text from distinct message IDs is not deduplicated', async ({ backend, fork }) => {
    const page = await fork.open('ready-chat');
    await page.goto('/chat');
    await waitForInteractive(page);
    const script = await backend.createChatScript([
      { messageId: 'first', text: '## Repeated\n\nSame' },
      { messageId: 'first', text: ' body.\n\n', waitForRelease: true },
      { messageId: 'second', text: '## Repeated\n\nSame' },
      { messageId: 'second', text: ' body.', waitForRelease: true },
    ]);
    await page.locator('.chat-input textarea').fill(script.prompt);
    await page.locator('.chat-input textarea').press('Enter');
    const assistant = page.locator('.assistant-message');
    await expect(assistant.locator('.rt-h2')).toHaveText('Repeated');
    await backend.releaseChatScript(script.id);
    await expect(assistant.locator('.rt-h2')).toHaveCount(2);
    await backend.releaseChatScript(script.id);

    await expect(assistant.locator('.streaming-cursor')).toHaveCount(0);
    await expect(assistant.locator('.rt-h2')).toHaveText(['Repeated', 'Repeated']);
    await expect(assistant.locator('p.rt-p')).toHaveText(['Same body.', 'Same body.']);
  });
});
