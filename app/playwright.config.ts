import { defineConfig, devices } from '@playwright/test';

const SERVER_URL = process.env.E2E_SERVER_URL ?? 'http://127.0.0.1:5199';
const CLIENT_URL = process.env.E2E_CLIENT_URL ?? 'http://127.0.0.1:5299';
const IS_CI = !!process.env.CI;

export default defineConfig({
  testDir: './e2e/tests',
  testIgnore: 'delegation.spec.ts', // Requires the isolated scripted delegation configuration.

  // E2E specs are long-form data-driven journeys. They must not depend on each other:
  // every test arranges its own ephemeral state (see e2e/fixtures.ts).
  fullyParallel: true,
  forbidOnly: IS_CI,

  // One retry on CI only: a green rerun still surfaces the flake in the report, but a
  // deterministic suite should never need it locally.
  retries: IS_CI ? 1 : 0,

  // Compartmentalized execution: each worker owns its own browser process.
  workers: IS_CI ? 2 : undefined,

  // Local: open the HTML report automatically, but only when something failed.
  // CI: produce a blob per shard so `--merge-reports` can assemble one HTML report.
  reporter: IS_CI
    ? [['blob'], ['github']]
    : [['html', { open: 'on-failure' }], ['list']],

  use: {
    baseURL: CLIENT_URL,

    // retain-on-failure keeps traces and screenshots out of normal local runs so the
    // working tree and disk stay clean; failures still capture everything needed.
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',

    actionTimeout: 10_000,
    navigationTimeout: 30_000,
  },

  // Cold prerender and settings initialization can exceed the default assertion timeout.
  // This readiness allowance does not prove WASM: Auto chooses it on later eligible visits.
  expect: { timeout: 15_000 },

  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],

  // The backend is arranged by the harness, not by the specs: an in-memory database and a
  // deterministic echo model, so no SQL Server container or Ollama install is required.
  // `reuseExistingServer` lets a developer keep the pair running across iterations.
  //
  // `gracefulShutdown` matters because `dotnet run` is a wrapper around the real server
  // binary: the default SIGKILL on the process group can leave the child holding the port.
  // SIGTERM lets the wrapper forward shutdown and reap its child. Ignored on Windows.
  webServer: [
    {
      command: 'dotnet run --project src/Server/AGUIWebChat.Server.csproj --no-launch-profile',
      url: `${SERVER_URL}/api/models`,
      reuseExistingServer: !IS_CI,
      gracefulShutdown: { signal: 'SIGTERM', timeout: 5_000 },
      timeout: 180_000,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: SERVER_URL,
        E2E__InMemoryDatabase: 'true',
        E2E__StubInference: 'true',
        ProtocolWorkflows__AllowedOrigin: CLIENT_URL,
        OLLAMA_ENDPOINT: 'http://127.0.0.1:1',
        OLLAMA_MODEL: 'e2e-stub',
      },
    },
    {
      command: 'dotnet run --project src/Client/AGUIWebChat.Client.csproj --no-launch-profile',
      url: CLIENT_URL,
      reuseExistingServer: !IS_CI,
      gracefulShutdown: { signal: 'SIGTERM', timeout: 5_000 },
      timeout: 180_000,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: CLIENT_URL,
        AGUI_SERVER_URL: SERVER_URL,
      },
    },
  ],
});
