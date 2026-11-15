# AGUI WebChat

A Blazor Web App (.NET 11, InteractiveAuto) chat application featuring Fluent UI, an Ollama agent exposed through AG-UI, SignalR telemetry, and an AI model catalogue backed by SQL Server.

[Repository overview](../README.md) · [Architecture](ARCHITECTURE.md)

## Project Structure

- `src/AGUIWebChat.AppHost/`: the Aspire AppHost that orchestrates SQL Server, Ollama, the server, and the client.
- `src/Client/`: Blazor Web App host shell (App.razor, Error.razor, ASP.NET Core pipeline) serving the InteractiveAuto render mode.
- `src/Client.Wasm/`: shared interactive UI, executed on Server initially and on WebAssembly on later eligible visits.
- `src/ServiceDefaults/`: shared health checks, OpenTelemetry, and service discovery.
- `src/Client.Wasm/Components/Pages/Chat.razor`: chat page and response cancellation.
- `src/Client.Wasm/Components/Chat/`: messages, settings, reasoning, and metrics.
- `src/Client.Wasm/Components/Pages/Models.razor`: AI model catalogue listing.
- `src/Client.Wasm/Services/`: AG-UI communication, settings, a SignalR telemetry connection per app instance, and the model catalogue API client.
- `src/Middleware/`: shared `AGUIWebChat.Middleware` library (logging interface, AG-UI event logger, UTF-8 SSE decoder, and read/write streams).
- `src/Client.Wasm/Middleware/` and `src/Server/Middleware/`: application-specific HTTP integrations and adapters that preserve existing logging categories.
- `src/Server/Agents/ChatAgentFactory.cs`: agent creation and instrumentation.
- `src/Server/Inference/`: inference settings and validation.
- `src/Server/Telemetry/`: response monitoring and metrics publication.
- `src/Server/Hubs/`: SignalR connection handling.
- `src/Server/Endpoints/`: model catalogue HTTP endpoints.
- `src/Server/Services/AI/`: model catalogue operations and business validation.
- `src/Server/Domain/AI/`: provider, model, default settings, and reasoning effort entities.
- `src/Server/Data/` and `src/Server/Migrations/`: EF Core database context and SQL Server migrations.
- `src/Server/Mapping/`: mappings between entities and API models.
- `src/Contracts/`: DTOs shared by the server and client.
- `tests/AGUIWebChat.Client.Tests/`: model panel validation and the client SSE logger.
- `tests/AGUIWebChat.Contracts.Tests/`: DTO annotations, boundary values, and validation messages.
- `tests/AGUIWebChat.Middleware.Tests/`: shared SSE decoding, stream cancellation, read errors, and byte preservation.
- `tests/AGUIWebChat.Server.Tests/`: inference, telemetry, catalogue services, SQLite persistence, API endpoints, and the server SSE logger adapter.
- `tests/Directory.Build.props`: shared .NET and xUnit test configuration.
- `tests/Directory.Build.targets` and `tests/test.runsettings`: shared results directory configuration for MTP and VSTest.
- `docs/`: project documentation.

All paths and commands below are relative to the repository root, which contains `AGUIWebChat.slnx` and `global.json`.

## The AG-UI packages this solution uses

AG-UI (Agent-User Interaction Protocol) is the open, event-based protocol that connects an
agentic backend to a user-facing frontend. It is one of three complementary agent protocols:
AG-UI for agent↔user interaction, MCP for agent↔tools, and A2A for agent↔agent.

The protocol is implemented in the official **AG-UI .NET SDK**, contributed by the .NET team
with CopilotKit and published on NuGet under the `AGUI.*` prefix. This solution references:

| Package | Version | Role here |
|---|---|---|
| `AGUI.Client` | 1.0.0 | `AGUIChatClient`, the client transport used by `UiAgentChatService` |
| `AGUI.Abstractions` | 1.0.0 | Protocol events and messages (`RunAgentInput`, `RunStartedEvent`, `ReasoningMessageContentEvent`) |
| `AGUI.Server` | 1.0.0 | Request recovery and per-endpoint tool/state stream mappings |
| `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` | 1.23.0-preview.260928.1 | `AddAGUIServer()` / `MapAGUIServer()` ASP.NET Core hosting glue |

`AGUI.Formatting` also appears in the restore graph transitively (via `AGUI.Client`) but is not
used by any code here. `AGUI.Protobuf` is the SDK's alternative wire transport; it is not
referenced by this solution at all, and this demonstration uses the default SSE transport
exclusively.

### Provenance: this solution already targets the current SDK

Microsoft Agent Framework previously carried its own copy of the AG-UI protocol in a package
named `Microsoft.Agents.AI.AGUI`. **That package has been removed.** The protocol now lives
solely in the AG-UI C# SDK, and the Agent Framework ASP.NET Core hosting package is layered
over `AGUI.Server`. The wire format is unchanged, so the split was source-level only. The
visible consequences for this codebase:

- `Microsoft.Agents.AI.AGUI` and the `AddAGUI()` / `MapAGUI()` method names do not exist.
  This solution uses `AddAGUIServer()` and `MapAGUIServer()`.
- The single old namespace split into `AGUI.Client`, `AGUI.Server`, and `AGUI.Abstractions`.
- `AGUIChatClient` takes an options object rather than positional serializer arguments.
- Server-side code recovers the originating request with `ChatOptions.TryGetRunAgentInput(out RunAgentInput)`.

> **Watch out:** `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` (current) and
> `Microsoft.Agents.AI.AGUI` (removed) are *different* packages with near-identical names.
> A grep for one will not find the other.

`Microsoft.Agents.AI` is on a stable 1.23.0, while the ASP.NET Core hosting package remains on
a `1.23.0-preview.*` build. That is expected: the hosting glue is still the framework-specific
layer, whereas the protocol itself is GA at `AGUI.*` 1.0.0.

The protocol SDK consolidates maintenance but adds no new protocol features. Everything this
repository demonstrates beyond the protocol itself — the reasoning stopwatch, Ollama token
accounting, and the SignalR metrics channel correlated by AG-UI `RunId` — is application code,
not SDK functionality. See [Architecture](ARCHITECTURE.md).

## Getting Started

The recommended way to run the application is the Aspire AppHost, which orchestrates the
server, the Blazor client, SQL Server, and the Ollama model in one command.

Prerequisites:

- The .NET 11 SDK (11.0.100-rc.2 or later; pinned in `global.json`).
- The Aspire CLI (`curl -sSL https://aspire.dev/install.sh | bash`, or
  `dotnet tool install -g Aspire.Cli`). Verify with `aspire doctor`.
- Docker, for the SQL Server container.
- Ollama installed locally, with the chat model pulled. Ollama runs as a native process
  so that it keeps hardware acceleration; a containerised Ollama is CPU-only and is not
  a substitute.

```bash
ollama pull qwen3:1.7b
aspire start
```

Aspire prints the dashboard URL on start. Open it to see resource state, health, logs,
and distributed traces. Use `aspire describe` for resource state from the CLI, and
`aspire wait <resource>` to block until a resource is healthy.

The server listens on `http://localhost:5100` and the client on `https://localhost:7219`.
The server exposes `/ag-ui`, `/telemetry`, `/api/models`, `/api/providers`, and the
ServiceDefaults health endpoints `/health` and `/alive`. Open `/models` in the client to
view the catalogue. The **Stop** button cancels the current chat request; leaving the
chat page also triggers cancellation.

### Scenario pages

The six typed scenario pages (Tools, Approvals, Shared state, Predictive state, Generative
UI, Reasoning) are thin `ScenarioShell` wrappers that each own a `ScenarioSession` and
`UIAgent`. Chat and Models are not: Chat drives `UiAgentChatService` directly, and Models
talks to `/api/models` with no agent at all. All pages inherit `InteractiveAuto`; children
do not override render modes.

| Page | Route | AG-UI endpoint | What it demonstrates |
|---|---|---|---|
| Chat | `/chat` | `/ag-ui` | Main streaming chat, reasoning display, telemetry, cancellation |
| Models | `/models` | `/api/models` | Catalogue CRUD against SQL Server |
| Tools | `/tools` | `/ag-ui/tools` | Server-owned tool calls rendered as typed cards |
| Approvals | `/approvals` | `/ag-ui/approval` | Human-in-the-loop approval of `switch_active_model` |
| Shared state | `/shared-state` | `/ag-ui/shared-state` | Whole-root `STATE_SNAPSHOT` driving a document editor |
| Predictive state | `/predictive-state` | `/ag-ui/predictive-state` | Proposed edit accepted or rejected via RFC 6902 delta |
| Generative UI | `/generative-ui` | `/ag-ui/generative-ui` | Plan checklist built from tool results and state patches |
| Reasoning | `/reasoning` | `/ag-ui/reasoning` | Reasoning-capable model with reasoning events |

`/` redirects to `/chat`. `/spike-ai` is the throwaway issue #3 probe page and is not a
product scenario.

### Optional: local oMLX

Start oMLX separately with its OpenAI-compatible API on `127.0.0.1:8001` and load a
model. The existing provider API is read-only, so add a provider to the catalogue database
once (there is no provider-creation UI):

```sql
INSERT INTO AIProviders (Name, ProviderType, Endpoint, IsEnabled)
VALUES ('oMLX', 'Omlx', 'http://127.0.0.1:8001/v1', 1);
```

Then use the Models page to add an enabled model for that provider whose model ID exactly
matches the ID served by oMLX. Select that model for a chat run. The server builds the same
per-run AG-UI agent using the selected provider; no additional service registration is required.

When **no enabled catalogue model** exists, set `OMLX_MODEL` on the server process to
use that model as the default. `OMLX_ENDPOINT` optionally overrides the default
`http://127.0.0.1:8001/v1`. An enabled catalogue model always takes precedence; the
explicit oMLX fallback then precedes configured Foundry and Ollama fallbacks. The
catalogue endpoint overrides `OMLX_ENDPOINT` for its provider. Set these variables in
the server's launch environment (including when launched by Aspire), not just the browser.

Only HTTP endpoints with a literal loopback IP and `/v1` path are accepted; hostnames,
credentials, queries, fragments, proxies and redirects are not supported. No API key is
sent. This is local-only, unauthenticated inference, not an isolation boundary; secure the
local server separately. Standard sampling settings are supported; Ollama-only thinking,
`top_k`, and `num_ctx` settings are dropped. Reasoning summaries and tool support depend
on the model and oMLX implementation and are not guaranteed.

### Optional: Microsoft Foundry

The application runs Ollama-only by default and never contacts Azure. Foundry is an
**optional** second provider. The commands below are documentation, not steps this
repository executes:

```bash
az login
az role assignment create \
  --assignee "$(az ad signed-in-user show --query id -o tsv)" \
  --role "Cognitive Services OpenAI User" \
  --scope "<foundry-resource-id>"

dotnet user-secrets set "Foundry:Endpoint" "https://<resource>.openai.azure.com/" \
  --project src/AGUIWebChat.AppHost
dotnet user-secrets set "Foundry:Model" "<deployment>" \
  --project src/AGUIWebChat.AppHost
```

`Foundry:ReasoningModel` is optional and selects the Responses API for reasoning
summaries. The AppHost forwards these values to the server as `FOUNDRY_URI`,
`FOUNDRY_MODEL`, and `FOUNDRY_REASONING_MODEL` only when an endpoint is present. See
[Microsoft Foundry provider](FOUNDRY.md) for the full configuration table, sampling
rules, and troubleshooting.

> **Not exercised here.** No Azure deployment, role assignment, or paid inference call
> was performed for this documentation. The Foundry path is implemented and unit-tested;
> it is not a verified cloud deployment.

### Configuration keys

| Key | Environment variable | Default | Purpose |
|---|---|---|---|
| `ConnectionStrings:ChatDatabase` | `ConnectionStrings__ChatDatabase` | localdb `AGUIWebChat` (Development: `localhost,1433`) | Catalogue database |
| `OLLAMA_ENDPOINT` | `OLLAMA_ENDPOINT` | — | Ollama base URL; falls back to `OLLAMA_HOST` |
| `OLLAMA_MODEL` | `OLLAMA_MODEL` | `qwen3:1.7b` | Default Ollama model name |
| `OMLX_ENDPOINT` | `OMLX_ENDPOINT` | `http://127.0.0.1:8001/v1` | Local oMLX API base URI |
| `OMLX_MODEL` | `OMLX_MODEL` | — | Opt-in fallback model when catalogue has no enabled model |
| `Foundry:Endpoint` | `FOUNDRY_URI` | — | Optional Foundry endpoint; must be absolute HTTPS |
| `Foundry:Model` | `FOUNDRY_MODEL` | — | Optional Foundry chat deployment |
| `Foundry:ReasoningModel` | `FOUNDRY_REASONING_MODEL` | — | Optional Foundry reasoning deployment |
| `Cors:AllowedOrigins` | `Cors__AllowedOrigins__0` | `[]` | Explicit allowed origins outside Development |
| `AGUI_SERVER_URL` | `AGUI_SERVER_URL` | `http://localhost:5100` | Backend URL for the relay, API clients, and telemetry |
| `E2E:InMemoryDatabase` | `E2E__InMemoryDatabase` | `false` | Use the in-memory catalogue database |
| `E2E:StubInference` | `E2E__StubInference` | `false` | Replace the provider with `ScriptedChatClient` |
| `Database:AllowStartupWithoutCatalogue` | `Database__AllowStartupWithoutCatalogue` | `false` | Start despite a failed catalogue readiness check |
| `MSSQL_SA_PASSWORD` | `MSSQL_SA_PASSWORD` | demo default in `docker-compose.yml` | `sa` password for the compose SQL Server (standalone path only) |
| `MSSQL_PORT` | `MSSQL_PORT` | `1433` | Host port mapped to the compose SQL Server (standalone path only) |

`MSSQL_SA_PASSWORD` and `MSSQL_PORT` are read by `docker-compose.yml`, not by the
application. The Aspire path does not use them: Aspire generates its own SQL Server
credentials and injects the connection string.

In Development, any loopback origin is allowed in addition to `Cors:AllowedOrigins`.
`AllowCredentials()` is required for the SignalR `/telemetry` negotiate request.

### Typed scenario client (#10)

The navigation includes Tools, Approvals, Shared state, Predictive state, Generative UI,
and Reasoning. The pages inherit `InteractiveAuto`; children do not override render modes.
Auto selects Server initially and WebAssembly on a **later eligible visit** after the
runtime is cached. It does not migrate an already-running Server circuit in place.

`ScenarioShell` owns a fresh `ScenarioSession`/`UIAgent` per typed scenario page. Rich output
follows `UIAgent -> FormattedChatClient -> ChatRunObserverClient -> AGUIChatClient ->
ScenarioAgUiTransport`. The transport observes real raw events before SDK conversion.
Generated Contracts metadata parses successful tool-result **JSON strings** into typed
cards. Whole-root snapshots and RFC6902 deltas drive the document editor and live plan
checklist; a patch is cloned, validated and committed atomically. Unknown root fields
and conversation inference settings survive checkpoints. Changing a setting affects
the next request; approving a model switch affects only this conversation's next run.

The pinned Components.AI package cannot assign identities to externally emitted custom
blocks (`ContentBlock.Id` has an internal setter). Instead, its supported `BlockRenderer`
presentation registrations render application-owned typed projections alongside the
real UIAgent rich transcript. This uses no reflection, dependency upgrade, invented
approval API, or raw JSON UI. Approve/reject and predictive accept/reject resume the
actual server-recorded MAF call, with its opaque interrupt and complete original args.
Cards remain pending/submitting until admitted execution and state events confirm the result.

#### Browser authority, relay and deployment

Both renderers use browser `fetch` against the **public client origin** `/ag-ui` path.
The existing Chat uses the same transport on `/ag-ui`, retaining its formatter, run
observer, settings, reasoning, telemetry and client SSE logging behavior.
The Server renderer uses a circuit-scoped JS bridge; WebAssembly uses the same bridge
contract. Pulls and checkpoint transfers are at most 8192 bytes, below the default
Server SignalR message limit, with incremental UTF-8-safe decoding and bounded SSE
frames. Stop/reset/navigation abort browser requests and unwind relay I/O.

The Client host relays only `/ag-ui` and the six listed scenario paths to the fixed
`AGUI_SERVER_URL` backend. POST must have JSON content and an exact same-origin `Origin`;
foreign/missing origins and `Sec-Fetch-Site: same-site/cross-site` are rejected before
upstream admission. This explicit Origin/JSON gate protects the new routes; the
ordinary Blazor antiforgery middleware alone does not. No GET, arbitrary destination,
redirect following, automatic retry, pooled cookie jar, or new account is introduced.
Every request's Cookie and each separate Set-Cookie, including deletion, pass unchanged.
Status, ProblemDetails, content type/encoding, progressive SSE and cancellation are preserved.
Cookies never enter JS values, checkpoint data, or application request logs.

`AGUI_SERVER_URL` **retains its backend meaning** for the relay, catalogue/provider API
clients, and SignalR telemetry. There is no `/api` or `/telemetry` proxy. For full
WebAssembly operation the configured backend URL must also be browser-reachable; an
HTTPS client requires HTTPS backend API and WSS telemetry to avoid mixed-content blocking.
The browser telemetry client uses native WebSockets without HTTP negotiation; deployment
must allow WebSocket upgrades to the existing `/telemetry` endpoint.

Plain HTTP Development/Testing uses the approved HttpOnly, host-only `/ag-ui` caller cookie
with SameSite=Lax. Production requires HTTPS and preserves Secure/SameSite=None, even
when the Production backend receives relay traffic over private HTTP. Run both deployed
hosts in the appropriate environment; never downgrade cookie attributes to make a
mixed HTTP/HTTPS deployment work. HTTPS at Kestrel is directly supported and tested.
With TLS termination, the client host must see the correct authenticated request scheme
and host **before** this gate through an explicitly configured, trusted ingress.
This change does not trust forwarded headers automatically: an untrusted
`X-Forwarded-Proto` cannot turn HTTP into HTTPS. Configure known proxies/networks and
production allowed hosts; do not enable unrestricted forwarded-header trust. Disable
response buffering at the ingress (`X-Accel-Buffering: no` is emitted), preserve each
Set-Cookie, and propagate downstream disconnects.

#### Tab-only continuity and recovery

`sessionStorage` holds only app-owned route/version, thread, whole state, canonical history,
typed presentation and pending tool/interrupt metadata. It is capped at 256 KiB per
route and expires after 30 minutes; active saves refresh the tab checkpoint, not the
approved server inactivity clock. There is no cookie value, shared localStorage
conversation authority, durable server history, or account. A later WASM visit restores
a **new** UIAgent without executing old turns or duplicating tool messages. Presentation
keys distinguish new tool occurrences even if a provider reuses protocol call IDs.
Every run/resume rebinds telemetry to the current renderer's live channel and fresh run ID,
without changing the original approval arguments or restored inference settings.

New conversation cancels and awaits the old run, releases subscriptions, clears the
route checkpoint and creates a new thread/agent. Generation guards prevent old callbacks
and saves from changing or resurrecting the new view. Corrupt/expired checkpoints,
pre-SSE typed errors and sanitized stream errors have visible recovery. Missing,
expired, consumed or foreign caller authority requires a fresh proposal, never an
optimistic success or automatic approval retry.

### Server scenario protocol (#9)

The existing `/ag-ui` chat is unchanged. All six scenario routes use `POST` with
`RunAgentInput` JSON and return `text/event-stream`, with JSON events on `data:` lines.
They reuse `ChatAgentFactory`, `LlmTelemetry`, and `ReasoningTelemetry`. Settings stay in
the existing `state` envelope; telemetry keeps the incoming `runId` and
`state.telemetryChannel`. Each scenario run resolves its catalogue model through
`IChatClientFactory`, without creating global active-model state.

| Endpoint | Tools and result/state contract |
|---|---|
| `/ag-ui/tools` | `list_available_models()` -> `ModelCatalogResult`; `get_model_details(modelId: int)` -> `ModelDetailsResult` (`found=false` for an absent row) |
| `/ag-ui/approval` | `switch_active_model(modelId: int)` -> approval interrupt; accepted result is `ModelSwitchResult` plus a `/modelId` JSON Patch |
| `/ag-ui/shared-state` | `update_document(title: string, text: string)` -> full state snapshot with `document: DocumentState` |
| `/ag-ui/predictive-state` | `propose_document_edit(title, text, originalTitle, originalText: string)` -> approval interrupt; accepted result is a document JSON Patch |
| `/ag-ui/generative-ui` | `create_plan(title: string, steps: string[])` -> full state snapshot with `plan: PlanResult`; `update_plan_step(stepId: string, status: string)` -> JSON Patch |
| `/ag-ui/reasoning` | Reasoning and answer events from the selected reasoning-capable catalogue model |

Tool scenarios require an enabled catalogue model and provider with `SupportsTools=true`.
Use `state.modelId` (a positive catalogue row ID), or omit it to use the enabled catalogue
default. The reasoning route rejects catalogue models with `ThinkingMode.None`.
Client-supplied `tools` must be empty: server scenarios own execution and approval.
Provider/settings validation failures use the SDK's terminal `RUN_ERROR` with
`code: "StreamingError"`; detailed exceptions remain server-side.

Example initial request (replace model IDs with enabled rows):

```json
{
  "threadId": "conversation-1",
  "runId": "run-1",
  "state": {
    "modelId": 7,
    "temperature": 0.25,
    "telemetryChannel": "0123456789abcdef0123456789abcdef",
    "document": { "title": "Original", "text": "Old text" }
  },
  "messages": [{ "id": "user-1", "role": "user", "content": "Switch to model 12." }],
  "tools": [],
  "context": []
}
```

For normal runs, expect `RUN_STARTED`, text/reasoning/tool events, and terminal
`RUN_FINISHED`. Tool calls emit `TOOL_CALL_START` (`toolCallId`, `toolCallName`),
`TOOL_CALL_ARGS` (`toolCallId`, JSON-string `delta`), and `TOOL_CALL_END`.
`TOOL_CALL_RESULT.content` is a string containing serialized result JSON, not an
embedded object. Parse it using the shared generated Contracts metadata.

State events follow the corresponding tool result. `STATE_SNAPSHOT.snapshot` is the
**whole** state object, preserving settings and unrelated root properties.
`STATE_DELTA.delta` is an RFC 6902 **array**, never a DTO-shaped pseudo-patch:

```json
[
  { "op": "replace", "path": "/plan/steps/0/status", "value": "done" }
]
```

Plan statuses are `pending`, `active`, `done`, and `blocked`; invalid IDs/statuses emit
a tool error and no state event. Approved model selection emits
`[{"op":"add","path":"/modelId","value":12}]` and takes effect on the **next** run after
the client applies the patch. It does not change catalogue rows or another conversation.
Predictive acceptance tests the current `/document` before adding its replacement;
a changed original title/text fails without a patch. Rejection never emits a state change.
Document tool arguments must be present strings, not `null`. Replacement titles must not be blank;
empty or whitespace-only document text is valid and is preserved exactly. Predictive
`originalTitle` and `originalText` must match the current document exactly, including
empty strings. Invalid arguments produce an explicit `Error:` tool result, leave the
document unchanged, and emit neither `STATE_SNAPSHOT` nor `STATE_DELTA`.

#### Approval/predictive cookies and continuation

These two routes retain only ephemeral MAF session state and pending approvals.
They issue `agui-caller`, an encrypted, server-generated random caller identity:
`HttpOnly`, `Path=/ag-ui`, `Max-Age=1800`, and no `Domain`. HTTPS and production responses
use `Secure; SameSite=None`, including production HTTP backends behind TLS termination.
Plain HTTP Development/Testing uses `SameSite=Lax` without `Secure`. Production clients
must use HTTPS. There is **no durable conversation storage** or authentication account.

The store is bounded to 256 caller/conversation/agent sessions and 256 KiB per session,
with a 64 KiB request limit. Sessions expire after **30 minutes of inactivity**; admitted
activity refreshes expiry. Caller cookie + `threadId` + endpoint identify a session.
`threadId` alone is not caller identity. Changing a thread starts a different conversation;
an approval cannot move to another thread, endpoint, or caller.

An initial approval run ends with `RUN_FINISHED.outcome.type="interrupt"` and
`outcome.interrupts[]`. Each interrupt has an opaque server-issued `id`,
`reason="tool_call"`, `toolCallId`, `message`, and `responseSchema`.
Recover the tool name and complete arguments from that call's tool events.
Do **not** derive the interrupt ID from the tool-call ID: transport IDs are unique even
when a provider reuses call IDs.

Resume the **same endpoint/thread/cookie**, retaining the current state and message
history, with a new `runId` and one explicit decision per pending interrupt:

```json
{
  "interruptId": "<exact interrupt.id>",
  "status": "completed",
  "payload": {
    "approved": true,
    "toolCall": {
      "callId": "<exact toolCallId>",
      "name": "switch_active_model",
      "arguments": { "modelId": 12 }
    }
  }
}
```

Place these objects in the next `RunAgentInput.resume` array. Reject with
`approved:false`; predictive resumes use `name:"propose_document_edit"` and all four
original string arguments, unchanged. The adapter translates opaque IDs back to the
original MAF-recorded requests; MAF approval-response binding stays enabled.
Decision IDs are consumed before execution. Concurrent/replayed resumes cannot execute
twice; cancellation after consumption requires a fresh proposal, not a retry of approval.
On rejection, `TOOL_CALL_RESULT.content` is `"Tool call invocation rejected."`, followed
by normal completion and no state patch.

Failures before streaming return `application/problem+json`, with `code` and
`restartRequired` extensions:

| HTTP | Codes | Recovery |
|---|---|---|
| 400 | `approval_thread_required`, `approval_invalid_input`, `approval_invalid_payload`, `approval_caller_missing`, `approval_caller_invalid` | Correct malformed input; for missing/invalid cookies clear/re-establish the cookie and request a fresh proposal |
| 409 | `approval_session_missing`, `approval_session_expired`, `approval_session_invalid`, `approval_invalid_or_replayed`, `approval_invalid_payload`, `approval_incomplete_decisions` | `restartRequired=true`; start a new conversation/proposal |
| 409 | `approval_pending` | `restartRequired=false`; accept/reject the outstanding proposal or reset |
| 413 | `approval_input_too_large` | Reduce request below 64 KiB and restart |
| 503 | `approval_store_full` | `restartRequired=false`; retry after inactive sessions expire |

An oversized session emits terminal SSE `RUN_ERROR` with
`code="approval_session_too_large"` and discards pending authority. Restart that
conversation; there is no successful `RUN_FINISHED` after this error.

Raw clients must retain a cookie jar across proposal/resume:

```bash
curl -N -c caller.cookies -b caller.cookies \
  -H 'Content-Type: application/json' \
  --data-binary @request.json http://localhost:5100/ag-ui/approval
```

Use the same jar with the resume request. Treat it as caller-sensitive, do not commit it,
and do not share it between unrelated callers.

**Browser integration:** the selected same-origin relay establishes caller authority in
the browser during Server rendering and retains it on later actual WASM visits. It
forwards each request's `Cookie` and each separate `Set-Cookie` unchanged, preserving
`/ag-ui`. There is no pooled/shared server cookie container or in-place circuit migration.
Scenario tab checkpoints restore thread/state/history and exact pending approval metadata
into a fresh real `UIAgent`; the approved approval/resume adapter uses the installed APIs.
See the browser authority and deployment section above for HTTPS, CSRF and backend
reachability requirements. SDK and curl clients still own their own isolated cookie jars.

### Database migrations are a separate step

Aspire provisions an empty SQL Server database, so the schema and the seeded
`qwen3:1.7b` catalogue row must be applied once per fresh container:

```bash
dotnet ef database update --project src/Server/AGUIWebChat.Server.csproj
```

The server project declares a singular `<TargetFramework>net11.0</TargetFramework>`, so
`--framework` is **not** needed. Passing it is harmless but redundant. The seed data
comes from the EF migrations (`SeedOllamaProvider`, `SeedOllamaChatModel`) and from
`ChatDbContext.OnModelCreating`, which seeds provider row `Id = 1` (`Ollama`).

### Startup database readiness check

Before the server serves traffic it checks the catalogue database and prints one
`DATABASE READINESS` block:

```
================================================================================
DATABASE READINESS: FAILED
================================================================================
status      : SchemaMissing
summary     : The database is reachable but the AI catalogue tables are absent.
provider    : Microsoft.EntityFrameworkCore.SqlServer
target      : data source=127.0.0.1,28297;initial catalog=ChatDatabase;user id=sa;password=***redacted***;...
detail      : SqlException: Invalid object name 'AIModels'.
remediation : Apply the schema and seed data: dotnet ef database update --project src/Server/AGUIWebChat.Server.csproj
              Then restart the server: aspire resource server start
              docs/README.md -> "Database migrations are a separate step"
================================================================================
```

The check answers four questions in order:

1. **Can the database be reached?** — `status: Unreachable` when the host refuses the
   connection or the database is absent.
2. **Does it carry the catalogue schema?** — `status: SchemaMissing` when `AIModels` does
   not exist. This is the cold-container case above.
3. **Is it migrated to this build?** — `status: MigrationsPending` lists the migrations in
   this assembly that the database has not recorded.
4. **Is anything usable left?** — a passing check still warns when no enabled model row
   exists, because startup then falls back to `OLLAMA_MODEL` or Foundry.

On failure the block is logged as `Fatal` and rethrown as `DatabaseNotReadyException`, so
the crash output carries the same remediation text. The connection string is redacted
(`password=***redacted***`) and is safe to paste into an issue, a log aggregator, or an
agent prompt.

Two details worth knowing:

- **`/health` reports the same check while the server runs.** A degraded catalogue makes
  `/health` return `503 Unhealthy` with the full block as the body, while `/alive` stays
  `Healthy`: orchestrators stop routing traffic without restarting the process.
- **`Database:AllowStartupWithoutCatalogue=true`** (`Database__AllowStartupWithoutCatalogue`)
  downgrades the failure to an error log and starts anyway. Catalogue endpoints then fail
  while configured Ollama/Foundry inference still works.

The check is provider-aware: the E2E in-memory database short-circuits to healthy, and the
migration probe runs only against SQL Server because SQLite E2E databases are built by
`EnsureCreated` and have no `__EFMigrationsHistory`.

### Running without Aspire

The standalone path still works and uses `docker-compose.yml` for SQL Server:

```bash
export MSSQL_SA_PASSWORD="<sa-password>"
docker compose up -d
export ConnectionStrings__ChatDatabase="Server=localhost,1433;Database=AGUIWebChat;User Id=sa;Password=<sa-password>;TrustServerCertificate=True;Encrypt=False"
export OLLAMA_ENDPOINT="http://localhost:11434"
export OLLAMA_MODEL="qwen3:1.7b"
dotnet run --project src/Server --launch-profile http
```

- `MSSQL_SA_PASSWORD` sets the `sa` password for the compose SQL Server. When it is unset,
  `docker-compose.yml` falls back to a demo default that is only suitable for local
  development. Set your own value and use the same value in the connection string. Do not
  commit it.
- `MSSQL_PORT` sets the host port mapped to the container (default `1433`). Change it if
  `1433` is already in use, and update the connection string to match.
- The password in the connection string above is a placeholder. Supply your own value.

In a second terminal:

```bash
export AGUI_SERVER_URL="http://localhost:5100"
dotnet run --project src/Client --launch-profile https
```

Note that the two paths use different database names (`ChatDatabase` under Aspire,
`AGUIWebChat` under compose), so they do not share catalogue data. The server reads
`ConnectionStrings:ChatDatabase` and falls back to `OLLAMA_HOST` when `OLLAMA_ENDPOINT`
is not set; Aspire injects the latter. SQLite is used only by the tests.

## Settings and Telemetry

Invalid chat inference settings fall back to their default values. Server-side limits are:

- Temperature: 0–2.
- Top-p: 0–1.
- Top-k: 1–1000.
- Context size: 1024–131072 tokens.
- Reasoning effort: `low`, `medium`, or `high`, selectable in Settings or with the button next to **Send**.

These application limits do not guarantee that the model or hardware can support the selected values.
Each Blazor circuit has its own connection and randomly generated telemetry channel, which is retained across SignalR reconnections. The server publishes only to that channel, without broadcasting to all clients. This mechanism separates circuits; it does not replace user authentication. Metrics are not replayed after a disconnection. A publication failure must not interrupt the model's response.

Each application stores its settings in `src/Client/appsettings*.json` or `src/Server/appsettings*.json`, with launch profiles in its `Properties/launchSettings.json` file. Keep secrets in environment variables or .NET user secrets. HTTP/SSE logs may contain conversations: configure logging levels and retention before shared use.

## Model Catalogue API

The model catalogue API (`/api/models`) returns errors as `application/problem+json`:

- `400`: invalid model data, unavailable provider, mismatched IDs, or malformed requests. Business validation responses include an `errors.model` array.
- `404`: the model requested by GET or PUT does not exist.
- `409`: the service detects a duplicate model for the provider.
- `500`: an unexpected error, with a neutral public message and a trace ID; internal details are logged on the server.

Deleting an absent model remains idempotent and returns `204`.

## Building and Testing

Run the following commands from the repository root:

```powershell
dotnet restore AGUIWebChat.slnx
dotnet build AGUIWebChat.slnx --configuration Release --no-restore
dotnet test --solution AGUIWebChat.slnx --configuration Release --no-build
```

The tests use xUnit v3 with Microsoft Testing Platform, configured in `global.json`. API tests supply their own Ollama configuration and an in-memory SQLite database. They require neither a running Ollama or SQL Server instance nor `OLLAMA_ENDPOINT` / `OLLAMA_MODEL` environment variables.

Run a single test project independently:

```powershell
dotnet test --project tests/AGUIWebChat.Client.Tests/AGUIWebChat.Client.Tests.csproj
dotnet test --project tests/AGUIWebChat.Contracts.Tests/AGUIWebChat.Contracts.Tests.csproj
dotnet test --project tests/AGUIWebChat.Middleware.Tests/AGUIWebChat.Middleware.Tests.csproj
dotnet test --project tests/AGUIWebChat.Server.Tests/AGUIWebChat.Server.Tests.csproj
```

The client tests reference the server project, so they can exercise server-side types directly. Contract and middleware tests reference only their corresponding libraries. SQLite and the ASP.NET Core test host packages are confined to the server test project. Shared SSE stream tests live in `AGUIWebChat.Middleware.Tests` and are not duplicated; application-specific logger adapters are tested in their respective client and server projects.

Test reports and attachments default to `tests/TestResults/`, which is excluded from Git. `tests/Directory.Build.props` defines the results path, and `tests/Directory.Build.targets` generates each test application's MTP configuration with `platformOptions.resultDirectory` during the build. The path is resolved from the repository's current location, so it works without a machine-specific path in source control. An explicit `--results-directory` option can override it.

For Visual Studio's VSTest mode with `xunit.runner.visualstudio`, the same shared props file selects `tests/test.runsettings`. If Visual Studio has a different solution-wide settings file selected, use **Test > Configure Run Settings > Select Solution Wide runsettings File** to select `tests/test.runsettings`. The MTP configuration is generated independently for Visual Studio's Microsoft Testing Platform mode. Rebuild the test projects after changing these settings. Existing results at the repository root are not moved automatically.

Coverage includes inference validation, fragmented SSE decoding, cancellation, read errors, telemetry, catalogue persistence, and API success and error responses. SQLite tests use `EnsureCreated`; they do not validate SQL Server migrations.

## Git

`.gitignore` excludes .NET build output, Visual Studio user files, and logs. `.gitattributes` normalizes text files to LF and Windows scripts to CRLF.

```powershell
git status
git add .
git diff --cached
git commit -m "Describe the change"
```
