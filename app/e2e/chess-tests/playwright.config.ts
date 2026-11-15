import { defineConfig, devices } from '@playwright/test';
import { resolve } from 'node:path';

// Dedicated opt-in configuration: never reuse an existing server or database.
const appRoot = resolve(__dirname, '../..');
const server = 'http://127.0.0.1:53991';
const client = 'http://127.0.0.1:53992';
export default defineConfig({
  testDir: '.',
  testMatch: 'chess.spec.ts',
  workers: 1,
  retries: 0,
  reporter: 'list',
  timeout: 210_000,
  use: { baseURL: client, ...devices['Desktop Chrome'], trace: 'off', screenshot: 'off', video: 'off' },
  webServer: [
    {
      cwd: appRoot,
      command: 'dotnet run --project src/Server/AGUIWebChat.Server.csproj --no-launch-profile',
      url: `${server}/api/chess/`, reuseExistingServer: false, timeout: 180_000,
      gracefulShutdown: { signal: 'SIGTERM', timeout: 10_000 },
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: server,
        E2E__InMemoryDatabase: 'true', E2E__StubInference: 'true',
        Chess__Enabled: 'true', Chess__ScriptPath: resolve(appRoot, '..', 'examples', 'chess.cs'),
      },
    },
    {
      cwd: appRoot,
      command: 'dotnet run --project src/Client/AGUIWebChat.Client.csproj --no-launch-profile',
      url: client, reuseExistingServer: false, timeout: 180_000,
      gracefulShutdown: { signal: 'SIGTERM', timeout: 10_000 },
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: client, AGUI_SERVER_URL: server },
    },
  ],
});
