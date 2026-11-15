import { defineConfig, devices } from '@playwright/test';
const server = 'http://127.0.0.1:5399';
const client = 'http://127.0.0.1:5499';
export default defineConfig({
  testDir: './e2e/tests', testMatch: 'delegation.spec.ts', workers: 1,
  reporter: 'list', timeout: 120000, expect: { timeout: 20000 },
  use: { baseURL: client, ...devices['Desktop Chrome'] },
  webServer: [
    { command: 'dotnet run --project src/Server/AGUIWebChat.Server.csproj --no-launch-profile', url: `${server}/api/models`, reuseExistingServer: false, timeout: 180000, gracefulShutdown: { signal: 'SIGTERM', timeout: 5000 }, env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: server, E2E__InMemoryDatabase: 'true', E2E__StubInference: 'true', E2E__DelegationScript: 'true', Delegation__AllowedOrigin: client, OLLAMA_ENDPOINT: 'http://127.0.0.1:1', OLLAMA_MODEL: 'e2e-stub' } },
    { command: 'dotnet run --project src/Client/AGUIWebChat.Client.csproj --no-launch-profile', url: client, reuseExistingServer: false, timeout: 180000, gracefulShutdown: { signal: 'SIGTERM', timeout: 5000 }, env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: client, AGUI_SERVER_URL: server, Delegation__AllowedOrigin: client } },
  ],
});
