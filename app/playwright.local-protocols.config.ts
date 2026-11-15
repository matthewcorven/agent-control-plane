import { defineConfig, devices } from '@playwright/test';

const server = 'http://127.0.0.1:5491';
const client = 'http://127.0.0.1:5492';

export default defineConfig({
  testDir: './e2e/tests',
  testMatch: 'local-protocols.spec.ts',
  outputDir: './test-results-local-protocols',
  reporter: 'list',
  use: { ...devices['Desktop Chrome'], baseURL: client },
  webServer: [
    {
      command: 'dotnet run --project src/Server/AGUIWebChat.Server.csproj --no-launch-profile',
      url: `${server}/api/models`, reuseExistingServer: false, timeout: 180000,
      gracefulShutdown: { signal: 'SIGTERM', timeout: 5000 },
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: server,
        E2E__InMemoryDatabase: 'true', E2E__StubInference: 'true',
        OLLAMA_ENDPOINT: 'http://127.0.0.1:1', OLLAMA_MODEL: 'e2e-stub',
        ProtocolWorkflows__AllowedOrigin: client },
    },
    {
      command: 'dotnet run --project src/Client/AGUIWebChat.Client.csproj --no-launch-profile',
      url: client, reuseExistingServer: false, timeout: 180000,
      gracefulShutdown: { signal: 'SIGTERM', timeout: 5000 },
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: client, AGUI_SERVER_URL: server },
    },
  ],
});
