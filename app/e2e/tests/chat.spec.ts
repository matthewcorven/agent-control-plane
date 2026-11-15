import { test, expect, waitForInteractive, clickFluentButton, fluentButton } from '../fixtures';

/**
 * Chat page.
 *
 * The server runs with `E2E:StubInference=true`, so the assistant reply is deterministic:
 * `ScriptedChatClient` echoes the last user message prefixed with `E2E echo: `.
 */

const PREFIX = 'E2E echo: ';

test.describe('chat page', () => {
  test('happy path: sending a message streams the deterministic echo reply', async ({ page }) => {
    await page.goto('/chat');
    await waitForInteractive(page);

    const input = page.locator('.chat-input textarea');
    const send = page.locator('.send-button');

    await input.fill('hello e2e');
    await clickFluentButton(send);

    // The user's own message is rendered immediately, before the stream completes.
    await expect(page.locator('.user-message')).toContainText('hello e2e');

    // The assistant message fills progressively and settles on the full echo.
    await expect(page.locator('.assistant-message')).toContainText(`${PREFIX}hello e2e`, {
      timeout: 15_000,
    });

    // Streaming finished: the cursor is gone and the input is usable again.
    await expect(page.locator('.streaming-cursor')).toHaveCount(0);
    await expect(send).toBeEnabled();
  });

  test('alternate path: Enter submits without touching the Send button', async ({ page }) => {
    await page.goto('/chat');
    await waitForInteractive(page);

    const input = page.locator('.chat-input textarea');

    await input.fill('enter key path');
    await input.press('Enter');

    await expect(page.locator('.assistant-message')).toContainText(`${PREFIX}enter key path`, {
      timeout: 15_000,
    });
  });

  test('alternate path: Shift+Enter inserts a newline instead of sending', async ({ page }) => {
    await page.goto('/chat');
    await waitForInteractive(page);

    const input = page.locator('.chat-input textarea');

    await input.fill('line one');
    await input.press('Shift+Enter');
    await input.type('line two');

    // Nothing was sent, so no message bubbles exist yet.
    await expect(page.locator('.user-message')).toHaveCount(0);
    await expect(input).toHaveValue(/line one\nline two/);
  });

  test('edge case: a blank message is not sent', async ({ page }) => {
    await page.goto('/chat');
    await waitForInteractive(page);

    const send = page.locator('.send-button');

    await page.locator('.chat-input textarea').fill('   ');
    await clickFluentButton(send);

    await expect(page.locator('.user-message')).toHaveCount(0);
    await expect(page.locator('.assistant-message')).toHaveCount(0);
  });

  test('edge case: a second message appends to the transcript', async ({ page }) => {
    await page.goto('/chat');
    await waitForInteractive(page);

    const input = page.locator('.chat-input textarea');
    const send = page.locator('.send-button');

    await input.fill('first');
    await clickFluentButton(send);
    await expect(page.locator('.assistant-message')).toContainText(`${PREFIX}first`, {
      timeout: 15_000,
    });

    await input.fill('second');
    await clickFluentButton(send);
    await expect(page.locator('.assistant-message')).toHaveCount(2, { timeout: 15_000 });
    await expect(page.locator('.assistant-message').last()).toContainText(`${PREFIX}second`);
  });

  test('edge case: the thinking-effort menu persists the chosen effort', async ({ page }) => {
    await page.goto('/chat');
    await waitForInteractive(page);

    const trigger = page.locator('#thinking-effort-menu');

    await expect(trigger).toBeVisible();
    await clickFluentButton(trigger);

    const item = page.getByRole('menuitem').filter({ hasText: /High|Medium|Low/ }).first();

    await expect(item).toBeVisible();

    // Read the expected label from the menu item, not the trigger: the trigger
    // still shows the previous effort until Blazor re-renders after the click.
    const chosen = (await item.textContent())?.replace('✓', '').trim() ?? '';
    expect(chosen).not.toBe('');

    await item.click();

    // Wait for the re-render so the reload assertion compares the new label.
    await expect(trigger).toContainText(chosen);

    await page.reload();
    await expect(page.locator('#thinking-effort-menu')).toContainText(chosen);
  });

  test('happy path: New chat clears the transcript', async ({ page }) => {
    await page.goto('/chat');
    await waitForInteractive(page);

    const input = page.locator('.chat-input textarea');
    const send = page.locator('.send-button');

    await input.fill('before reset');
    await clickFluentButton(send);
    await expect(page.locator('.assistant-message')).toContainText(`${PREFIX}before reset`, { timeout: 15_000 });

    await clickFluentButton(fluentButton(page, 'New chat'));

    await expect(page.locator('.user-message')).toHaveCount(0);
    await expect(page.locator('.assistant-message')).toHaveCount(0);
    await expect(input).toBeEnabled();
  });

  test('alternate path: a message sent after New chat starts a fresh transcript', async ({ page }) => {
    await page.goto('/chat');
    await waitForInteractive(page);

    const input = page.locator('.chat-input textarea');
    const send = page.locator('.send-button');

    await input.fill('first thread');
    await clickFluentButton(send);
    await expect(page.locator('.assistant-message')).toContainText(`${PREFIX}first thread`, { timeout: 15_000 });

    await clickFluentButton(fluentButton(page, 'New chat'));
    await expect(page.locator('.assistant-message')).toHaveCount(0);

    await input.fill('second thread');
    await clickFluentButton(send);

    await expect(page.locator('.user-message')).toHaveCount(1);
    await expect(page.locator('.assistant-message')).toHaveCount(1);
    await expect(page.locator('.assistant-message')).toContainText(`${PREFIX}second thread`, { timeout: 15_000 });
    await expect(page.locator('.assistant-message')).not.toContainText('first thread');
  });

  test('edge case: New chat on an empty transcript is a no-op', async ({ page }) => {
    await page.goto('/chat');
    await waitForInteractive(page);

    await clickFluentButton(fluentButton(page, 'New chat'));

    await expect(page.locator('.user-message')).toHaveCount(0);
    await expect(page.locator('.assistant-message')).toHaveCount(0);

    // The page is still usable afterwards.
    const input = page.locator('.chat-input textarea');

    await input.fill('after empty reset');
    await clickFluentButton(page.locator('.send-button'));
    await expect(page.locator('.assistant-message')).toContainText(`${PREFIX}after empty reset`, { timeout: 15_000 });
  });
});
