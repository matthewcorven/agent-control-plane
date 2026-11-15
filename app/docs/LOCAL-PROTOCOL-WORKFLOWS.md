# Local protocol workflows

The `/local-protocols` page offers three bounded demonstrations:

- **A2A streaming:** starts a temporary loopback-only echo agent, discovers its real A2A agent card, validates its HTTP+JSON endpoint, and consumes SDK streaming events. The browser receives the collected events after the operation, not incrementally.
- **A2A background:** sends an SDK background task to the same local echo agent and polls it to completion.
- **MCP:** connects a real MCP client and server through in-process streams, lists `add_numbers`, and invokes it. This is a direct tool call, not model-selected Harness execution.

No arbitrary remote endpoints are accepted. The echo agent is deterministic, not model inference. The temporary A2A listener is local but is not an authentication boundary against other local processes. Calls have bounded input, event count and timeouts; concurrent endpoint operations are limited to one. OpenAI-compatible hosting is not implemented. See `OPENROUTER-WORKFLOW.md` for the separate browser-to-OpenRouter workflow and its limitations.

## Registration and configuration

The server references the protocol project, maps `MapLocalProtocolWorkflows()`, and navigation links to both pages are registered. The endpoint maps **only in Development** and only when configuration `ProtocolWorkflows:AllowedOrigin` is set to the exact loopback HTTP(S) frontend origin, for example `http://127.0.0.1:5492`. Configure it using the application's normal configuration mechanisms; it is not a credential. Development CORS allows loopback origins; the frontend's `AGUI_SERVER_URL` must resolve to the loopback backend.

The endpoint additionally requires a loopback client, loopback Host, exact Origin and custom header; these protect browser use but do not authenticate local processes. Do not expose the backend publicly or treat the origin check as authentication. The dedicated Playwright test exercises all three local workflows with a configured exact origin.

## Focused checks

```sh
dotnet run --project app/tests/ProtocolWorkflows.Tests/ProtocolWorkflows.Tests.csproj
node app/tests/protocol/openrouter.test.mjs
cd app && npx playwright test --config playwright.local-protocols.config.ts
```

The solution and protocol projects' NuGet vulnerability audit reported no vulnerable packages with the current .NET 11 SDK. A separate plain-SDK compatibility consumer of the server can resolve `Microsoft.Data.SqlClient`'s `System.Security.Cryptography.Pkcs` 9.0.18 dependency and trigger GHSA-846f-f764-76mx and GHSA-2fmv-rv2c-59x7; that scratch consumer was not the app's dependency graph. Such consumers should pin at least 9.0.21 and audit their own restore graph. Keep deployed runtimes patched and audit published artifacts separately.
