import { defineConfig } from '@playwright/test';
import base from './playwright.config';

export default defineConfig({
  ...base,
  testDir: './e2e/local-audio-tests',
  testMatch: 'local-audio.spec.ts',
  use: { ...base.use, baseURL: 'http://127.0.0.1:5298' },
  webServer: [
    { command: 'node e2e/local-audio-mock.mjs', url: 'http://127.0.0.1:5399/state', reuseExistingServer: false },
    {
      command: 'dotnet run --project src/Server/AGUIWebChat.Server.csproj --no-launch-profile',
      url: 'http://127.0.0.1:5198/api/models', reuseExistingServer: false, timeout: 180_000,
      gracefulShutdown: { signal: 'SIGTERM', timeout: 5_000 },
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: 'http://127.0.0.1:5198',
        E2E__InMemoryDatabase: 'true', E2E__StubInference: 'true', OLLAMA_ENDPOINT: 'http://127.0.0.1:1',
        OLLAMA_MODEL: 'e2e-stub', LocalAudio__Endpoint: 'http://127.0.0.1:5399/v1/' },
    },
    {
      command: 'dotnet run --project src/Client/AGUIWebChat.Client.csproj --no-launch-profile',
      url: 'http://127.0.0.1:5298', reuseExistingServer: false, timeout: 180_000,
      gracefulShutdown: { signal: 'SIGTERM', timeout: 5_000 },
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: 'http://127.0.0.1:5298',
        AGUI_SERVER_URL: 'http://127.0.0.1:5198' },
    },
  ],
});
