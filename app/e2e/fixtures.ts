import { test as base, expect, type APIRequestContext, type BrowserContext, type Locator, type Page } from '@playwright/test';

/**
 * Ephemeral, deterministic test data.
 *
 * Every fixture arranges its own backend state through the real HTTP API and tears it down
 * afterwards, so specs never share records and can run in parallel across forks.
 */

export interface TestModel {
  id: number;
  providerId: number;
  modelId: string;
  displayName: string;
  supportsTools?: boolean;
  thinkingMode?: string;
}

export interface Backend {
  request: APIRequestContext;
  createModel(overrides?: Partial<TestModel>): Promise<TestModel>;
  deleteModel(id: number): Promise<void>;
  listModels(): Promise<TestModel[]>;
  createChatScript(steps: ChatScriptStep[]): Promise<ChatScript>;
  releaseChatScript(id: string): Promise<void>;
  chatScriptStatus(id: string): Promise<{ calls: number; cancelled: boolean; results: string[] }>;
}

export interface ChatScriptStep {
  messageId: string;
  text?: string;
  waitForRelease?: boolean;
  error?: string;
  tool?: string;
  arguments?: Record<string, unknown>;
  reasoning?: string;
}

export interface ChatScript {
  id: string;
  prompt: string;
}

/**
 * Predecessor-state forking.
 *
 * `capture` snapshots a page's cookies and localStorage with `storageState()`. `open` starts a
 * fresh browser context seeded with that snapshot, so a long multi-step journey (login, wizard,
 * saved settings) can be replayed as the starting point of many independent tests instead of
 * being re-executed in each one. Each fork is isolated, so forks run in parallel safely.
 */
export interface StateFork {
  capture(page: Page, name: string): Promise<void>;
  open(name: string, options?: { cold?: boolean }): Promise<Page>;
}

type Fixtures = {
  backend: Backend;
  /** A model created for this test only and removed when it ends. */
  seededModel: TestModel;
  fork: StateFork;
};

export const test = base.extend<Fixtures>({
  backend: async ({ playwright }, use) => {
    // Fixtures arrange data against the AG-UI server directly, not through the Blazor host.
    const serverURL = process.env.E2E_SERVER_URL ?? 'http://127.0.0.1:5199';
    const request = await playwright.request.newContext({ baseURL: serverURL });
    const scripts = new Set<string>();

    const backend: Backend = {
      request,

      async createModel(overrides = {}) {
        const unique = `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;

        const response = await request.post('/api/models', {
          data: {
            providerId: 1,
            modelId: overrides.modelId ?? `e2e-model-${unique}`,
            displayName: overrides.displayName ?? `E2E Model ${unique}`,
            thinkingMode: overrides.thinkingMode ?? 'None',
            isEnabled: true,
            supportsVision: false,
            supportsTools: overrides.supportsTools ?? false,
            supportsStreaming: true,
            reasoningEfforts: overrides.thinkingMode === 'Effort' ?
              ['low', 'medium', 'high'].map((value, sortOrder) => ({
                displayName: value, value, sortOrder, isDefault: value === 'medium',
              })) : [],
          },
        });

        expect(response.status(), `POST /api/models -> ${await response.text()}`).toBe(201);

        return (await response.json()) as TestModel;
      },

      async deleteModel(id) {
        const response = await request.delete(`/api/models/${id}`);

        // 404 is acceptable: the spec under test may have deleted the record already.
        expect([204, 404], `DELETE /api/models/${id}`).toContain(response.status());
      },

      async listModels() {
        const response = await request.get('/api/models');

        expect(response.ok()).toBeTruthy();

        return (await response.json()) as TestModel[];
      },

      async createChatScript(steps) {
        const response = await request.post('/api/e2e/chat-scripts/', { data: steps });
        expect(response.ok(), `POST chat script -> ${await response.text()}`).toBeTruthy();
        const script = (await response.json()) as ChatScript;
        scripts.add(script.id);
        return script;
      },

      async releaseChatScript(id) {
        const response = await request.post(`/api/e2e/chat-scripts/${id}/release`);
        expect(response.status()).toBe(204);
      },

      async chatScriptStatus(id) {
        const response = await request.get(`/api/e2e/chat-scripts/${id}`);
        expect(response.status()).toBe(200);
        return response.json();
      },
    };

    try {
      await use(backend);
    } finally {
      const cleanup = await Promise.allSettled([...scripts].map(async (id) => {
        const response = await request.delete(`/api/e2e/chat-scripts/${id}`);
        expect(response.status()).toBe(204);
      }));
      try {
        const failures = cleanup.filter((result) => result.status === 'rejected');
        if (failures.length > 0) {
          throw new AggregateError(failures.map((result) => result.reason), 'Chat script cleanup failed');
        }
      } finally {
        await request.dispose();
      }
    }
  },

  seededModel: async ({ backend }, use) => {
    const model = await backend.createModel();

    await use(model);

    await backend.deleteModel(model.id);
  },

  fork: async ({ browser, baseURL }, use) => {
    const captured = new Map<string, Awaited<ReturnType<BrowserContext['storageState']>>>();
    const tabStates = new Map<string, Record<string, string>>();
    const contexts: BrowserContext[] = [];

    await use({
      async capture(page, name) {
        captured.set(name, await page.context().storageState());
        tabStates.set(name, await page.evaluate(() => Object.fromEntries(
          Object.entries(sessionStorage).filter(([key]) => key.startsWith('agui-scenario:')))));
      },

      async open(name, options = {}) {
        const storageState = captured.get(name);

        if (!storageState) {
          throw new Error(`No captured state named "${name}". Capture it before forking.`);
        }

        const context = await browser.newContext({ baseURL, storageState: options.cold ?
          { cookies: storageState.cookies, origins: [] } : storageState });
        await context.addInitScript((state) => {
          for (const [key, value] of Object.entries(state))
            if (sessionStorage.getItem(key) === null) sessionStorage.setItem(key, value);
        }, tabStates.get(name) ?? {});

        contexts.push(context);

        return context.newPage();
      },
    });

    await Promise.all(contexts.map((context) => context.close()));
  },
});

/**
 * Fluent UI buttons.
 *
 * `<FluentButton>` renders a `<fluent-button>` custom element. Only icon-only buttons get an
 * explicit `role="button"`, so `getByRole('button', { name })` does not find text buttons.
 * Match on the element and its trimmed text instead.
 */
export function fluentButton(page: Page, name: string) {
  return page.locator('fluent-button').filter({ hasText: new RegExp(`^\\s*${name}\\s*$`) });
}

/**
 * Click a Fluent UI button.
 *
 * Assert usability, then perform a real pointer click without custom-element stability or
 * trailing navigation waits. Native focus/blur commits the field's change before OnClick.
 */
export async function clickFluentButton(locator: Locator) {
  await expect(locator).toBeVisible();
  await expect(locator).not.toHaveAttribute('disabled', '');
  await locator.click({ force: true, noWaitAfter: true });
}

export async function resetScenario(page: Page) {
  const shell = page.locator('.scenario-page');
  await expect(shell).toHaveAttribute('data-thread', /\S+/);
  const thread = await shell.getAttribute('data-thread');
  expect(thread).not.toBeNull();
  await clickFluentButton(fluentButton(page, 'New conversation'));
  await expect(shell).not.toHaveAttribute('data-thread', thread!);
  await expect(shell).toHaveAttribute('data-thread', /\S+/);
  await expect(shell).toHaveAttribute('data-resetting', 'false');
}

/**
 * Wait until the page is interactive.
 *
 * The app renders with `InteractiveAuto`: the first response is a static prerender, then the
 * selected interactive runtime initializes settings. The placeholder disappearing means the
 * layout is ready, not that WebAssembly is running. Auto chooses WASM on later eligible
 * document visits; runtime tests must assert the actual renderer separately.
 */
export async function waitForInteractive(page: Page, timeout?: number) {
  await expect(page.locator('.loading-settings')).toHaveCount(0, { timeout });
}

export async function waitForWebAssembly(page: Page) {
  const deadline = Date.now() + 60000;
  const remaining = () => Math.max(1, deadline - Date.now());
  if (page.url() === 'about:blank')
    await page.goto('/spike-ai', { timeout: Math.min(30000, remaining()) });
  await waitForInteractive(page, remaining());
  // Auto writes this hash only after loading its real bootstrap. Navigating while
  // it is pending restarts the cold, serial download and can prevent eligibility.
  await expect.poll(() => page.evaluate(() =>
    localStorage.getItem('blazor-resource-hash:AGUIWebChat.Client.Wasm')),
  { timeout: remaining() }).not.toBeNull();
  await page.goto('/spike-ai', { timeout: Math.min(30000, remaining()) });
  await expect(page.locator('#spike-phase-value')).toHaveText('wasm', { timeout: remaining() });
  expect(Date.now(), 'Actual WebAssembly must initialize within the shared 60-second budget').toBeLessThanOrEqual(deadline);
}

export { expect };
