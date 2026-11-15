import { test, expect, waitForInteractive, clickFluentButton } from '../fixtures';
import type { Page } from '@playwright/test';

/**
 * Fluent UI block renderers.
 *
 * `ScriptedChatClient` replies with a Markdown sample when the user message starts with
 * `markdown:`. The client parses that Markdown into a `RichTextNode` tree and renders it
 * through `RichTextRenderer`, so these assertions prove the structured renderer is live
 * rather than the `MarkupString` fallback.
 */

const SENTINEL = 'markdown:';

async function sendMarkdown(page: Page) {
  await page.goto('/chat');
  await waitForInteractive(page);

  await page.locator('.chat-input textarea').fill(`${SENTINEL} show me blocks`);
  await clickFluentButton(page.locator('.send-button'));

  const assistant = page.locator('.assistant-message');
  await expect(assistant.locator('.rt-h2')).toContainText('Markdown sample', { timeout: 15_000 });

  return assistant;
}

test.describe('fluent block renderers', () => {
  test('happy path: Markdown renders as structured rich-text blocks', async ({ page }) => {
    const assistant = await sendMarkdown(page);

    await expect(assistant.locator('.rt-h2')).toHaveText('Markdown sample');
    await expect(assistant.locator('p.rt-p').first()).toContainText('bold');
    await expect(assistant.locator('strong')).toContainText('bold');
    await expect(assistant.locator('em')).toContainText('italic');
    await expect(assistant.locator('.rt-inline-code')).toHaveText('inline code');

    await expect(assistant.locator('ul.rt-list > li.rt-li')).toHaveCount(2);
    await expect(assistant.locator('ul.rt-list > li.rt-li').first()).toContainText('first item');

    await expect(assistant.locator('.rt-code-card')).toHaveCount(1);
    await expect(assistant.locator('.rt-code-lang')).toHaveText('csharp');
    await expect(assistant.locator('.rt-code-text')).toContainText('var answer = 42;');

    await expect(assistant.locator('.rt-table-card table.rt-table')).toHaveCount(1);
    await expect(assistant.locator('.rt-table tr').nth(1)).toContainText('alpha');
  });

  test('alternate path: a plain echo reply still renders through the structured renderer', async ({ page }) => {
    await page.goto('/chat');
    await waitForInteractive(page);

    await page.locator('.chat-input textarea').fill('plain echo');
    await clickFluentButton(page.locator('.send-button'));

    const assistant = page.locator('.assistant-message');
    await expect(assistant).toContainText('E2E echo: plain echo', { timeout: 15_000 });

    // Plain text parses to a single paragraph node, not the MarkupString fallback.
    await expect(assistant.locator('p.rt-p')).toHaveCount(1);
    await expect(assistant.locator('p.rt-p')).toHaveText('E2E echo: plain echo');
    await expect(assistant.locator('.rt-h2')).toHaveCount(0);
  });

  test('edge case: streaming settles without leaving a cursor or partial blocks', async ({ page }) => {
    const assistant = await sendMarkdown(page);

    await expect(page.locator('.streaming-cursor')).toHaveCount(0);
    await expect(page.locator('.send-button')).toBeEnabled();

    // The final tree is complete: exactly one heading, one list, one code card, one table.
    await expect(assistant.locator('.rt-h2')).toHaveCount(1);
    await expect(assistant.locator('ul.rt-list')).toHaveCount(1);
    await expect(assistant.locator('.rt-code-card')).toHaveCount(1);
    await expect(assistant.locator('.rt-table-card')).toHaveCount(1);
  });
});
