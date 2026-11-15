# AGUI WebChat

A Blazor Web App (.NET 11, InteractiveAuto) chat application featuring Fluent UI, an Ollama
agent exposed through AG-UI, SignalR telemetry, and an AI model catalogue backed by SQL Server.
Microsoft Foundry is an optional second provider.

## Documentation

- [Project guide](docs/README.md): setup, configuration, building, and testing.
- [Architecture](docs/ARCHITECTURE.md): component map, provider resolution, telemetry flow, findings and limitations.
- [The AG-UI packages this solution uses](docs/README.md#the-ag-ui-packages-this-solution-uses): SDK provenance, package versions, and the removed `Microsoft.Agents.AI.AGUI` package.
- [Blazor AI components spike](docs/AI-COMPONENTS-SPIKE.md): go/no-go for `Microsoft.AspNetCore.Components.AI` under `InteractiveAuto` (issue #3).
- [Microsoft Foundry provider](docs/FOUNDRY.md): configuration, identity setup, reasoning, and troubleshooting for the optional Azure OpenAI provider (issue #5).
- [E2E testing](docs/E2E.md): Playwright standards, fixtures, and selector notes.

## Quick start

Prerequisites: the .NET 11 SDK pinned in `global.json`, the Aspire CLI, Docker for the SQL
Server container, and a native Ollama install with the chat model pulled.

```bash
ollama pull qwen3:1.7b
aspire start
```

Then apply the catalogue schema once per fresh container:

```bash
dotnet ef database update --project src/Server/AGUIWebChat.Server.csproj
```

The server checks this on startup and prints a `DATABASE READINESS` block naming that
exact command if the schema is missing, so the failure is self-explanatory. `/health`
reports the same check while the server runs. See
[database readiness](docs/README.md#startup-database-readiness-check).

The server listens on `http://localhost:5100` and the client on `https://localhost:7219`.
See the [project guide](docs/README.md#getting-started) for the standalone (non-Aspire)
path, configuration keys, and the optional Foundry setup.

## Scenario pages

| Page | Route | AG-UI endpoint |
|---|---|---|
| Chat | `/chat` | `/ag-ui` |
| Models | `/models` | `/api/models` |
| Tools | `/tools` | `/ag-ui/tools` |
| Approvals | `/approvals` | `/ag-ui/approval` |
| Shared state | `/shared-state` | `/ag-ui/shared-state` |
| Predictive state | `/predictive-state` | `/ag-ui/predictive-state` |
| Generative UI | `/generative-ui` | `/ag-ui/generative-ui` |
| Reasoning | `/reasoning` | `/ag-ui/reasoning` |

## Repository Structure

- `src/`: Aspire AppHost, client (host shell + WebAssembly), server, shared contracts, and middleware.
- `tests/`: automated tests (xUnit v3 on Microsoft Testing Platform).
- `e2e/`: Playwright end-to-end specs and fixtures.
- `docs/`: detailed documentation.

Open `AGUIWebChat.slnx` to work with the solution.

## Building and testing

```bash
dotnet restore AGUIWebChat.slnx
dotnet build AGUIWebChat.slnx --configuration Release --no-restore
dotnet test --solution AGUIWebChat.slnx --configuration Release --no-build
```

The test suite requires neither Ollama nor SQL Server. The Playwright suite runs against a
deterministic in-memory backend with stub inference; it does not prove real model inference
or SQL Server migrations. See [E2E testing](docs/E2E.md).

The [approved file workflow](docs/FILE-WORKFLOW.md) can use a local Harness agent to suggest edits to browser-selected text, with separate reviews before submission and download. The [chess workflow](docs/CHESS.md) shows the coordinator and A2A worker sessions. Both are local proofs of concept, not sandboxed security boundaries.

### Harness delegation

The `/delegation` example runs a coordinator and up to three awaited Harness specialist
sessions. The view reports submitted prompts and final responses, not internal tool
transcripts. Runs are memory-only, expire after 15 minutes, and are cancelled after two
minutes. An interrupted browser start may run until that deadline.

Set `Delegation__AllowedOrigin` on **both** client and server to the exact loopback browser
origin (for example `http://localhost:5245`); an unset or invalid value denies requests.
The client proxies bounded delegation and file-agent operations to its configured loopback backend. The
capability remains in browser module memory and is not stored or included in URLs. Browser
navigation attempts cancellation; shutdown awaits the actual child work. An external model
may receive the submitted prompts.

Run the isolated scripted integration (real Harness agents, no provider calls) with
`npx playwright test -c playwright.delegation.config.ts`; the script requires Development,
`E2E__StubInference`, `E2E__InMemoryDatabase`, and `E2E__DelegationScript` together.
