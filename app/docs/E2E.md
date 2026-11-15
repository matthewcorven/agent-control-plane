# End-to-End Testing Standards

This repository uses **Playwright** as the only E2E UI automation tool. The harness lives
in `e2e/`, the configuration in `playwright.config.ts`, and the CI pipeline in
`.github/workflows/e2e.yml`.

## Quick start

Use the .NET SDK selected by the repository's `global.json`. CI reads that same
file through `setup-dotnet`; while the SDK is prerelease, `dotnet-quality: preview`
allows its `latestFeature` channel to resolve without a separate version pin.

```bash
npm ci
npx playwright install --with-deps chromium
npm run e2e            # headless, opens the HTML report only on failure
npm run e2e:headed     # watch the browser
npm run e2e:ui         # Playwright UI mode
```

The config boots both halves of the app itself:

| Process | Project | Default URL |
| --- | --- | --- |
| API + AG-UI | `src/Server` | `http://127.0.0.1:5199` |
| Blazor host | `src/Client` | `http://127.0.0.1:5299` |

Both run with `E2E__InMemoryDatabase=true` and `E2E__StubInference=true`, so no SQL Server
and no Ollama install are required. `reuseExistingServer` keeps a warm pair alive between
local iterations; CI always starts fresh.

## Coverage gate

**No UI feature or UI modification may be committed or pushed without E2E coverage.**
A change is not reviewable until the suite exercises it. Every user workflow must be
demonstrated by a long-form, deterministic, data-driven test that arranges its own
ephemeral state.

Each workflow needs all three path classes:

- **Happy path** — the intended journey end to end.
- **Alternate path** — a legitimate variation (keyboard instead of mouse, replace-history
  navigation, a second message, a different entry point).
- **Edge case** — the boundary that breaks naive implementations (blank input, unknown
  route, empty collection, duplicate submission).

## Red-Green-Refactor

Work test-first. Write the failing E2E test that describes the new behaviour (**red**),
make the smallest change that turns it green (**green**), then clean up with the test
still passing (**refactor**). A test written after the implementation only proves the
implementation matches itself.

## Determinism

- No `waitForTimeout` as a synchronisation primitive. Assert on observable state.
- No shared mutable fixtures. Every test arranges its own records through the `backend`
  fixture and cleans up after itself.
- No dependence on execution order. `fullyParallel: true` is a hard requirement, so any
  test that only passes in sequence is a bug in the test.
- The LLM is stubbed (`ScriptedChatClient`) and echoes the last user message with a
  stable `E2E echo: ` prefix. Assertions target that contract, never a real model.

### Controlled transcript streams

`backend.createChatScript(steps)` creates a script owned by the current test. Send its
returned `prompt` through the chat UI. Steps carry a `messageId`, a text delta or an
error, reasoning delta or real tool call, and an optional `waitForRelease` checkpoint. Wait for observable output,
then call `backend.releaseChatScript(id)` to continue the stream. This exercises the
real AG-UI server, client adapter, UIAgent and block renderer without sleeps or paid
inference. Each script runs once. The fixture attempts all script deletions and cancels
pending checkpoints during teardown, including after a failed test.

Cancellation status records an actual cancelled step/checkpoint exception before it
propagates, not a token callback that iterator unwind can unregister. A completed
stream is not cancellation. `cancellation.spec.ts` exercises real tool continuations
in Server and WebAssembly: native Stop, reset, document navigation and successful
release, with backend execution/result counts and subsequent live renderer canaries.
Stop and reset also assert the browser fetch abort; document replacement can detach
that browser event, so navigation still requires the backend cancellation assertion.

The script endpoints exist only in Development when both `E2E:StubInference` and
`E2E:InMemoryDatabase` are enabled. Normal provider registration and inference are
unchanged outside that harness.

`e2e/tests/transcript.spec.ts` covers cumulative updates, partial-answer cancellation
and errors, and multiple distinct message IDs. Completion status is rendered separately
from answer nodes; each distinct block contributes its latest snapshot in arrival order.
Failed runs show a generic retry message, not raw upstream exception details.

## Compartmentalization and parallel execution

Alternate paths and edge cases must not replay a long predecessor journey. Capture the
predecessor state once and fork it into the isolated test:

```ts
import { test, expect } from '../fixtures';

test('edge case: the wizard resumes at step 3', async ({ page, fork }) => {
  await fork.capture(page, 'wizard-step-2');   // runs the predecessor once
    const alternate = await fork.open('wizard-step-2'); // same test's isolated context
    await alternate.goto('/wizard');           // checkpoint restored, no journey replay
});
```

`fork.capture` keeps `browserContext.storageState()` in the **current test's in-memory**
fixture. Because Playwright storageState excludes sessionStorage, it explicitly captures
only app-owned `agui-scenario:` checkpoints as well. `fork.open` creates a fresh context
seeded with those snapshots; it never shares mutable state between tests or workers.
Checkpoint seeds are applied only when absent, not on every document visit. Use
`fork.open(name, { cold: true })` to preserve caller cookies/tab history while omitting
localStorage runtime eligibility markers for a genuine cold Server restore.

### Scenario and transport contracts

`scenarios.spec.ts` exercises typed tools, real approval/rejection, exact shared-document
edits, predictive accept/reject/stale-original failure, snapshot/delta checklists and
reasoning. `transport.spec.ts` proves browser-originated `/ag-ui` requests in Server,
host-only HttpOnly cookie creation, a later **actual WebAssembly** visit with the same
thread/pending approval, fresh run IDs, exact resume arguments, foreign caller isolation,
concurrent/replayed decisions, progressive Unicode output, backend cancellation and
the real Server OnClick canary. It also checks explicit relay CSRF/JSON/route rejection.

`scenario-recovery.spec.ts` covers large cold Server checkpoints, reset and handle
cleanup, missing caller recovery, real 400/413 before inference, invalid/expired tab
checkpoints, reused provider IDs, and reasoning alternate/edge paths. Its invalid-patch
case corrupts exactly one delta from an **actually executed** endpoint response and
asserts real backend tool results plus unchanged browser state; it does not substitute
mock successful SSE for execution.
Truncation and malformed-result cases likewise damage an actually executed response.
`typed-checkpoint-recovery.spec.ts` validates semantic pending approval and predictive
arguments in both cold Server and actual WebAssembly. It changes only the pending tool's
arguments in an app-owned checkpoint, then asserts visible restore failure, checkpoint
clearing, no admitted action, and a real new-conversation/send canary. Valid controls
retain opaque approval arguments, unknown inference state and exact empty/whitespace
document strings. Live cases damage real proposal responses, including missing argument
fields and a missing tool-call end, to prove unchecked interrupts cannot reach rendering.
Reset must publish a renderable document root atomically, including after these errors.
`resetScenario` waits for both a new thread and completion of the explicit reset status;
awaiting a click alone does not await an asynchronous Blazor reset.
It first waits for the existing non-empty scenario thread before reading its identity.
Layout settings readiness does not await a newly navigated scenario's checkpoint initialization.
After the native reset it requires a non-empty replacement thread as well as the changed
identity and completed reset status. `scenario-readiness.spec.ts` holds a fetched real
transport-module response until a separate live predecessor completes a reset/send canary.
Cold restored, cold fresh and actual WebAssembly restored forks prove that pending
initialization cannot complete the helper, and that subsequent sending uses the fresh thread.
The unsent-draft test also waits for the original non-empty thread before capturing it;
layout readiness alone can still expose a connecting scenario shell. Its held-module
regression verifies that capture stays pending until real initialization completes, then
checks the cleared draft, changed thread and live send on the replacement conversation.

Never infer WebAssembly from theme mutation, a loading placeholder, or elapsed time.
Assert real `RendererInfo.Name` and `OperatingSystem.IsBrowser()` evidence. Auto chooses
a renderer on later eligible visits, not through an in-place Server circuit migration.
`waitForWebAssembly` keeps the current document alive until Auto writes its own
`blazor-resource-hash:AGUIWebChat.Client.Wasm` bootstrap-completion marker, then opens
one subsequent `/spike-ai` document and asserts actual WASM. The marker is read-only
readiness evidence, not renderer proof. Both stages share the existing 60-second
budget. Repeated navigation during cold, serial bootstrap downloads can prevent
eligibility from completing, especially on CI.
`runtime-bootstrap.spec.ts` holds one real WASM assembly response until a separate
live predecessor completes a reset/send canary. Cold and warm-storage forks must
not navigate away from that pending bootstrap, and must then preserve real WASM,
thread history, browser-origin transport, telemetry and fresh-conversation sending.
There are no render-mode overrides, fabricated markers or fixed delays.
Large Unicode/checkpoint cases exceed 32 KiB while
each JS interop pull remains bounded to 8192 bytes.

Client contract tests additionally check all RFC6902 operations, atomic invalid patches,
strict pointers/array indices and size bounds. Real ephemeral Kestrel relay tests check
HTTP/HTTPS, separate Set-Cookie/deletion, no cookie retention, default-port Origin,
untrusted forwarded-proto rejection, unchanged status/body, no redirects/retries and
progressive relay unwind on physical disconnect. Real AG-UI E2E tests separately assert
backend cancellation and execution counts. TLS certificates exist only in the test
process and are pinned by that test; no OS trust setting is changed.

Backend state is arranged the same way: the `backend` fixture talks to the API directly,
so a test can create exactly the records it needs instead of inheriting whatever another
worker left behind.

`telemetry-settings.spec.ts` uses per-test catalogue rows and scripts in actual Server
and WebAssembly. It verifies the UIAgent settings envelope at the browser relay, correlated
provider/model monitoring for non-reasoning replies, `ShowModelInfo`, next-run conversation
selection, a deleted selection with no silent fallback, and a fresh live send/reset canary.
Alternate and edge paths fork the captured ready state through the existing fixture.

## Artifacts

| Where | What | Retention |
| --- | --- | --- |
| Local | `playwright-report/`, `blob-report/`, `test-results/` | ephemeral, gitignored |
| CI | `playwright-report` artifact | 90 days, hardcoded |

Locally the HTML report opens automatically **only on failure**
(`reporter: [['html', { open: 'on-failure' }]]`). Traces, screenshots and video are
`retain-on-failure`, so a green run leaves no disk bloat.

CI shards the suite, uploads one blob per shard, merges them into a single HTML report,
and uploads that report with `if: always()` and `retention-days: 90`. The pipeline never
commits reports, traces or screenshots back to any branch.

## Retrieving a CI report

Use the `playwright-report-retrieval` plugin from
[`matthewcorven/corven-agent-tools`](https://github.com/matthewcorven/corven-agent-tools):

```bash
python3 scripts/fetch_report.py --run-id 123456789 --test "Login Flow"
python3 scripts/fetch_report.py --commit abc1234
```

It downloads the artifact into `./playwright-reports/<run-id>/`, prunes directories older
than 7 days, and opens the report deep-linked to the requested test
(`index.html#?q=Login%20Flow`). Pass `--no-open` to skip the browser launch.

## Selector notes for this app

Fluent UI v5 renders custom elements, which breaks several Playwright defaults:

- `<FluentButton>` becomes `<fluent-button>`. Text buttons have **no** `role="button"`, so
  `getByRole('button', { name })` will not match. Use the `fluentButton(page, name)` helper
  from `e2e/fixtures.ts`.
- Stability waits on the custom button host can stall. `clickFluentButton` first checks
  visibility and the disabled attribute, then uses a real pointer click with forced
  positioning and `noWaitAfter`. Native focus/blur commits the field change before the
  real Blazor OnClick. Backend execution and circuit-liveness assertions remain required.
- `toBeDisabled()` / `toBeEnabled()` read the native `disabled` property and do not
  understand custom elements. Use `toHaveAttribute('disabled', '')`.
- `DialogService.ShowDrawerAsync` renders a `<fluent-drawer>` whose host has no layout box.
  Assert on its children, and prefer `getByRole('textbox', { name })` over `getByLabel`
  (the latter matches both the `fluent-text-input` host and its shadow `<input>`).
- `#main-menu` renders twice (desktop sidebar and mobile drawer). Scope with
  `.locator('visible=true')`.
- `locator.fill()` does not fire a native `change` event, so `@bind-Value` never syncs.
  Components that must react to typing use `ChangeAfterKeyPress`.
