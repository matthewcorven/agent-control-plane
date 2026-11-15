# Agent Prompt: Playwright E2E Alignment Specification

Portable implementation specification for aligning any code repository with the
Playwright E2E convention used by `AGUIWebChat`.

**How to use this document:** paste §0–§7 into the target repository as the agent
prompt. §8 is the evidence inventory for the reference implementation and is not
part of the prompt.

---

## §0 Mission and hard constraints

You are implementing Playwright E2E testing in this repository. Three outcomes are
required and no others:

1. E2E runs locally, writes **ephemeral** artifacts, and never dirties the working tree.
2. CI runs the same suite sharded, and publishes a **persistent** `playwright-report`
   artifact with 90-day retention, whether or not the run passed.
3. Any agent can then retrieve that CI report by run ID or commit SHA through the
   `playwright-report-retrieval` plugin from `matthewcorven/corven-agent-tools`.

Hard constraints:

- **Playwright is the only permitted E2E UI automation tool.** Do not add Selenium,
  Cypress, WebdriverIO, Puppeteer harnesses, or bespoke browser-driving scripts. If one
  already exists, migrate it; do not run both.
- **Do not vendor, copy, or fork `corven-agent-tools`.** Reference it; never restate it.
- **Do not commit reports, traces, videos, or screenshots.** Ever, on any branch.
- **Do not invent a second reporting convention.** The artifact name is fixed:
  `playwright-report`.

---

## §1 Three-layer ownership model

Every file in this convention belongs to exactly one layer. When in doubt, ask: *would
this text be identical in an unrelated repository using a different language?* If yes it
is layer C. If no it is layer A. If it is about who does the work, it is layer B.

### Layer A — repository-local (this repo owns it, must be authored here)

| Artifact | Why it cannot be shared |
| --- | --- |
| `package.json` scripts + `@playwright/test` devDependency | Node toolchain is repo-specific |
| `playwright.config.ts` | Ports, `webServer` commands, workers, projects are app-specific |
| `.gitignore` artifact block | Combined with unrelated ignores |
| `.github/workflows/e2e.yml` | Build/boot steps are repo-specific |
| `e2e/fixtures.ts` | Talks to this app's backend API and selectors |
| `e2e/tests/*.spec.ts` | Assert this app's behaviour |
| `docs/E2E.md` | Local commands, ports, readiness rules, selector adapters |
| `.github/copilot-instructions.md` E2E section | Must point at the plugin's skill, not restate it |
| `.github/copilot/settings.json` | Enables the marketplace + plugin for this repo |

### Layer B — Squad-declared (only if `.squad/` exists in this repo)

These files assign and gate work. They **must not** carry E2E standards, selector rules,
reporter config, or artifact policy — that is layer A/C and duplicating it there creates
a second, drifting source of truth.

| File | What it may say about E2E |
| --- | --- |
| `.squad/team.md` | The tester member exists and is active |
| `.squad/routing.md` | `Tests and coverage` routes to the tester |
| `.squad/agents/<tester>/charter.md` | One bullet claiming E2E/Playwright ownership; pointer to `docs/E2E.md` only |
| `.squad/ceremonies.md` | Test-failure / reviewer-rejection ceremony condition |
| `.squad/decisions.md` | Accepted durable decisions, if any were made |

Repositories without `.squad/` implement layers A and C only and are fully compliant.

### Layer C — `corven-agent-tools` (consumed, never modified from a target repo)

| Surface | Path | Role |
| --- | --- | --- |
| Marketplace manifest | `.github/plugin/marketplace.json` | Registers plugin `playwright-report-retrieval` v0.1.0 |
| Plugin manifest | `plugins/playwright-report-retrieval/plugin.json` | Plugin identity and keywords |
| Skill (canonical standards) | `plugins/playwright-report-retrieval/skills/playwright-report-retrieval/SKILL.md` | Tooling constraint, Red-Green-Refactor, coverage gate, determinism, forking, reporting, retrieval |
| MCP server | `plugins/playwright-report-retrieval/server.py` | Exposes `fetch_report`, `list_failed_tests` |
| MCP registration | `plugins/playwright-report-retrieval/mcp.json` | `stdio`, `python3 ${PLUGIN_ROOT}/server.py` |
| CI CLI | `plugins/playwright-report-retrieval/scripts/fetch_report.py` | Run/commit resolution, artifact download, 7-day prune, deep link, browser open |
| Default artifact name | `DEFAULT_ARTIFACT_NAME = "playwright-report"` | Binding contract for the CI job |

### Precedence rule

The plugin skill is **canonical** for policy. `docs/E2E.md` is **canonical** for this
app's adapters. On conflict the plugin wins on policy and this repo wins on mechanics.
Squad files never win on either.

---

## §2 Layer A deliverables (required file set)

### 2.1 `package.json`

```json
{
  "private": true,
  "description": "E2E harness for <PROJECT> (Playwright). See docs/E2E.md for the standards.",
  "scripts": {
    "e2e": "playwright test",
    "e2e:headed": "playwright test --headed",
    "e2e:ui": "playwright test --ui",
    "e2e:report": "playwright show-report playwright-report",
    "e2e:install": "playwright install --with-deps chromium"
  },
  "devDependencies": { "@playwright/test": "<pinned latest stable>" }
}
```

Pin the version exactly. Do not float with `^` — the report format must be stable
across the shard/merge boundary.

### 2.2 `playwright.config.ts` — required keys

| Key | Required value | Reason |
| --- | --- | --- |
| `testDir` | `./e2e/tests` | Separates journeys from unit tests |
| `fullyParallel` | `true` | Hard requirement; order-dependent tests are bugs |
| `forbidOnly` | `!!process.env.CI` | `test.only` must not reach CI |
| `retries` | `CI ? 1 : 0` | A green rerun still shows the flake in the report |
| `workers` | reduced under CI | Keeps shard memory predictable |
| `reporter` (local) | `[['html', { open: 'on-failure' }], ['list']]` | Ephemeral, opens only on failure |
| `reporter` (CI) | `[['blob'], ['github']]` | Blob enables `merge-reports` |
| `use.trace` | `'retain-on-failure'` | No green-run disk cost |
| `use.screenshot` | `'only-on-failure'` | Same |
| `use.video` | `'retain-on-failure'` | Same |
| `use.baseURL` | from env with a default | Same config in both modes |
| `expect.timeout` | raised above the default | Prerender/cold-start readiness |
| `webServer` | every process the app needs | The harness boots the app, not the specs |
| `webServer[].reuseExistingServer` | `!IS_CI` | Warm loop locally, always fresh in CI |
| `webServer[].gracefulShutdown` | `SIGTERM` when the runner wraps a child | Avoid orphaned port holders |
| `projects` | at least Chromium | One browser keeps the suite affordable |

The env-driven split must be a single `IS_CI` constant so local and CI cannot drift:

```ts
const IS_CI = !!process.env.CI;
reporter: IS_CI ? [['blob'], ['github']] : [['html', { open: 'on-failure' }], ['list']],
```

`webServer` must supply deterministic test doubles (in-memory store, stub inference,
unreachable real endpoints) so no external service or paid call is required.

### 2.3 `.gitignore` — exact block

```gitignore
# Playwright: reports and traces are ephemeral, never committed
playwright-report/
blob-report/
test-results/
playwright-reports/
```

`playwright-reports/` is the retrieval plugin's cache namespace; it must be ignored too.

### 2.4 `.github/workflows/e2e.yml` — required job shape

Two jobs, `test` and `report`, in one workflow named `E2E (Playwright)`.

```yaml
name: E2E (Playwright)
on:
  push: { branches: [main] }
  pull_request:
  workflow_dispatch:
permissions:
  contents: read
env:
  CI: 'true'
```

`test` job:

- `strategy.matrix.shard: [1, 2]`, `total: [2]`, `fail-fast: false`
- `timeout-minutes: 30`
- install toolchain → restore → **build once before Playwright boots** (the
  `webServer` readiness window must not pay for a cold compile) → `npm ci` →
  `npx playwright install --with-deps chromium`
- run `npx playwright test --shard=${{ matrix.shard }}/${{ matrix.total }}`
- `always()`: dump and upload server logs (`retention-days: 7`) — `webServer` stdout
  is not forwarded into the Actions log, so a server-side exception is otherwise invisible
- `always()`: upload `blob-report/` as `blob-report-${{ matrix.shard }}`
  with `retention-days: 90` and `if-no-files-found: ignore`

`report` job:

- `needs: [test]`, `if: always()`, `timeout-minutes: 10`
- download `blob-report-*` with `merge-multiple: true`
- `npx playwright merge-reports --reporter html ./all-blob-reports`
- `always()`: upload `playwright-report/` with **`name: playwright-report`** and
  **`retention-days: 90`**

`name: playwright-report` and `retention-days: 90` are not tunable — they are the
contract the retrieval plugin depends on. `retention-days: 90` is the provider ceiling.

Add `concurrency` with `cancel-in-progress: true` so superseded pushes stop burning
runners.

### 2.5 `e2e/fixtures.ts` — required contract

Export `test` and `expect` from one module; every spec imports from `'../fixtures'`.
The default export surface must include:

| Export | Contract |
| --- | --- |
| `test` (extended `base`) | Provides every fixture below |
| `expect` | Re-exported so specs import from one place |
| `backend` | Arranges data through the **real** app API, not the UI; must delete everything it created during teardown, including after a failed test |
| `fork` | `capture(page, name)` snapshots `browserContext.storageState()`; `open(name, opts?)` returns a Page in a fresh context seeded from that snapshot |
| `seeded*` | Per-test records removed at teardown |
| selector helpers | App-specific; see §2.7 |

Rules the fixtures must enforce:

- **No shared mutable state.** `fork.capture` writes into the current test's in-memory
  fixture, never a shared directory.
- **`storageState` is not sufficient on its own.** It excludes `sessionStorage`; capture
  any app-owned checkpoint keys explicitly.
- **Seed defensively.** Apply restored seeds only when absent, never on every document
  visit.
- **Provide an opt-out for cold starts** (for example `fork.open(name, { cold: true })`)
  so a "genuinely cold restore" case is expressible.
- **Every script/scenario a fixture creates runs once** and is cancelled at teardown.

### 2.6 `e2e/tests/*.spec.ts` — coverage shape

One spec file per user workflow, named for the workflow. Each workflow supplies all
three path classes: **happy path**, **alternate path**, **edge case**.

Alternate and edge tests fork the predecessor state instead of replaying the journey:

```ts
test('edge case: the wizard resumes at step 3', async ({ page, fork }) => {
  await fork.capture(page, 'wizard-step-2');
  const alternate = await fork.open('wizard-step-2');
  await alternate.goto('/wizard');
});
```

Forbidden in specs: `waitForTimeout`, cross-test ordering, shared mutable globals, real
inference or paid third-party calls, and asserting on mock output rather than the stub's
documented contract.

### 2.7 `docs/E2E.md` — allowed content

Only what is specific to this app:

- quick start with this repo's commands and ports
- the `webServer` process table and the deterministic flags it sets
- the stub's documented contract (prefix, echo behaviour, endpoint availability)
- app readiness rules (prerender vs interactive vs renderer proof)
- selector adapters for the UI framework (why role queries fail, what to use instead)
- a link to the plugin skill for policy

**Not allowed in `docs/E2E.md`:** a restatement of the coverage gate, determinism rules,
report retention numbers, or retrieval CLI usage. Link to the plugin instead. Duplicated
policy drifts; this document already had to be de-duplicated once.

### 2.8 `.github/copilot-instructions.md` — allowed content

Keep the E2E section to: this repo's mandate line, the coverage gate, Red-Green-Refactor,
determinism, compartmentalization, the never-commit-artifacts rule, and the command to
run before pushing. Point at `docs/E2E.md` and the plugin skill for detail.

### 2.9 `.github/copilot/settings.json` — required entries

```json
{
  "enabledPlugins": {
    "playwright-report-retrieval@corven-agent-tools": true
  },
  "extraKnownMarketplaces": {
    "corven-agent-tools": {
      "source": { "source": "github", "repo": "matthewcorven/corven-agent-tools" }
    }
  }
}
```

Enabling the plugin is what makes the documented retrieval path reproducible from repo
config alone. Recall §8.5: in the reference repo this entry was missing even though the
docs told readers to use the plugin.

---

## §3 Layer B call-outs (Squad-declared)

Apply only when `.squad/` exists. Keep every edit minimal and pointer-shaped.

1. **`.squad/team.md`** — the tester row exists and is `✅ Active`. Adding a member is a
   squad operation; do not hand-edit the roster without the coordinator flow.
2. **`.squad/routing.md`** — a row mapping testing/coverage work to the tester, e.g.
   `| Tests and coverage | Zoe | #12 |`. E2E and unit testing route to the same owner;
   do not create a second testing owner.
3. **`.squad/agents/<tester>/charter.md`** — one ownership bullet such as
   `- E2E/Playwright checks, edge cases, regression gating`. The charter must not contain
   reporter config, retention numbers, artifact names, or selector rules. It may name
   `docs/E2E.md` and the plugin skill as the sources of truth.
4. **`.squad/ceremonies.md`** — the failure ceremony condition may name test failure, e.g.
   `build failure, test failure, or reviewer rejection`. No ceremony may redefine the
   coverage gate.
5. **`.squad/decisions.md`** — E2E policy changes land here as durable decisions. The
   reference repo currently has **no** E2E decision recorded even though the convention
   exists; do not treat absence as authorization to change policy.
6. **`.squad/config.json`** — read `stateBackend` before writing anything under `.squad/`.
   A non-local backend means runtime-owned state must go through the state bridge, never
   through direct file writes.
7. **`.gitignore`** — Squad's runtime ignores and the Playwright block are separate
   sections. Do not merge them; they are owned by different tools.

**Anti-pattern to reject:** copying `docs/E2E.md` policy into a charter "so the agent
knows." The charter is read by the routing layer; the standards are read on demand. A
charter that restates policy becomes a stale second copy the moment the plugin changes.

---

## §4 Layer C usage (consumed, never modified here)

### 4.1 Retrieval surfaces

| Surface | Invocation | Use |
| --- | --- | --- |
| MCP `fetch_report` | tool call | Download and extract a report, return a deep link |
| MCP `list_failed_tests` | tool call | Enumerate failures from a fetched report or `report_dir` |
| CLI | `python3 scripts/fetch_report.py --run-id <id>` | Agent or human workflow |
| CLI | `python3 scripts/fetch_report.py --commit <sha>` | Resolve the run from a commit |
| CLI | `--test "<name>"` | Deep link `#?q=<urlencoded>` |
| CLI | `--test-id <id>` | Deep link `#?testId=<id>` |
| CLI | `--no-open`, `--json` | Non-interactive / structured output |
| CLI | `--force` | Re-download, bypassing the cache |

Behaviour to rely on: extraction to `./playwright-reports/<run-id>/` (override with
`--namespace` or `PLAYWRIGHT_REPORT_NAMESPACE`), pruning older than 7 days
(`--max-age-days`, `0` disables), cache-first reads that work offline or after artifact
expiry, and `gh` CLI as the only current provider via the `PROVIDERS` seam.

### 4.2 What a target repo must never do with layer C

- Copy `server.py` or `fetch_report.py` into the repo.
- Rename the artifact away from `playwright-report`.
- Change retention away from 90 days to match the plugin "better".
- Add a second retrieval script that drifts from the deep-link format.
- Patch the plugin from inside a target repo. Fix it in `corven-agent-tools` and release.

### 4.3 Operational rules for MCP callers

Two rules that are not obvious from the tool schema and that cost real debugging time
during the 0.2.0 validation. Both are consumer-side obligations.

**Always pass `repo` explicitly.** The MCP server is a stdio process whose working
directory is whatever the CLI happened to spawn it in — the plugin install directory, a
temp directory, or a directory that no longer exists. It is never the target checkout.
`list_failed_tests(run_id=…)` without `repo` therefore always fails from an MCP client,
even when you are sitting inside the repository. The CLI layer is different: it inherits
your shell's cwd and can infer the repo from the git remote.

**Restart the CLI after `copilot plugin update`.** MCP servers are long-lived and hold the
plugin code in memory. Updating the files on disk does not update a running server. The
signature of a stale server is a split result: the CLI layer passes (it re-reads from
disk) while the MCP layer fails on the same input. If you see that split, restart before
concluding the fix did not land.

**Pass `workflow` when resolving by commit.** A commit SHA can belong to several workflow
runs. Without `workflow`, resolution prefers a run that actually carries the requested
artifact, but naming the workflow is unambiguous and cheaper. `workflow` accepts a file
name (`e2e.yml`) or a numeric id.

**Pass `namespace` if the default is unreliable.** `list_failed_tests` resolves its cache
directory from the server's cwd when no `namespace` is given. An explicit `namespace`
removes that dependency entirely and is the safe choice in any long-lived MCP session.

**Never accept `total: 0` as a pass.** A report can contain zero tests and still be a
failure: a `global-setup` abort produces exactly that, and Playwright marks it
`stats.ok: true`, so `errors[]` is the only signal. Gate on `total > 0 && failed_count == 0`,
and treat a non-empty `errors` array or a `warning` field as a failure. Before `0.2.3` this
was a silent false-clean (DEF-9); the plugin now surfaces it, but the consumer-side
assertion is what actually protects the pipeline.

**Know which report shape you are reading.** Playwright changed the embedded payload
between minor versions, and the plugin had to be taught both:

| Playwright | Payload location | Decoded shape |
|---|---|---|
| 1.64.x | `<template id="playwrightReportBase64">` inside `<body>` | one `<fileId>.json` per spec file, each with `tests[]` |
| 1.57.x | `<script id="playwrightReportBase64" type="application/zip">` **after `</html>`** | a single `report.json` with top-level `files[]`, `stats`, `errors` |

If you decode a report by hand, accept both. If you pin Playwright, pin it deliberately —
a floating `^` range can move you across this boundary without any config change.

---

## §5 Adaptation seam (stack-specific choices)

**Must change per repository:** language toolchain and build commands, `webServer`
commands/ports/env, `expect.timeout`, test-double strategy, selector helpers in
`e2e/fixtures.ts`, spec names, and the app readiness rules in `docs/E2E.md`.

**Must not change:** reporter split, `retain-on-failure` policy, gitignore block,
artifact name, retention days, shard/merge job shape, `fullyParallel`, the three path
classes, the coverage gate, or the plugin reference.

Suggested defaults when the stack differs: keep `shard: [1, 2]` until the suite exceeds
roughly 10 minutes per shard, then widen; keep `timeout-minutes: 30` per shard; keep
Chromium-only until a cross-browser claim is actually being tested.

---

## §6 Acceptance checklist

The work is not complete until every line below is verifiable.

```bash
# 1. Suite resolves and counts
npm run e2e -- --list | tail -1

# 2. Config has the CI split and failure-only retention
rg -n "blob" playwright.config.ts
rg -n "retain-on-failure" playwright.config.ts

# 3. Artifacts are ignored
rg -n "^(playwright-report|blob-report|test-results|playwright-reports)/$" .gitignore

# 4. Workflow contract
rg -n "name: playwright-report" .github/workflows/e2e.yml
rg -n "retention-days: 90" .github/workflows/e2e.yml
rg -n "merge-reports" .github/workflows/e2e.yml
rg -n "if: always\(\)" .github/workflows/e2e.yml

# 5. Plugin enabled from repo config
python3 -c "import json,sys; \
d=json.load(open('.github/copilot/settings.json')); \
print('playwright-report-retrieval@corven-agent-tools' in d['enabledPlugins'])"

# 6. Nothing under .squad/ restates E2E policy
rg -n -i "retention-days|blob-report|merge-reports|retain-on-failure" .squad/ || echo "clean"
```

Also confirm by reading, not just grepping:

- every workflow has happy, alternate, and edge coverage
- `fork.capture`/`fork.open` is used instead of journey replay
- no `waitForTimeout` anywhere in `e2e/**`
- `docs/E2E.md` links to the plugin skill rather than restating policy
- the working tree is clean after `npm run e2e` on a green run

Functional proof for CI: dispatch the workflow, then fetch the report by run ID and
confirm a deep link to one of your test names opens.

---

## §7 Anti-patterns to reject

1. Copying plugin content into the target repo ("so it does not need the plugin").
2. Writing E2E standards into `.squad/` charters or routing files.
3. Committing `playwright-report/`, traces, or screenshots "temporarily".
4. Renaming the artifact, lowering retention, or skipping `if: always()`.
5. Asserting against real inference, a live clock, or a paid API.
6. Replaying a long predecessor journey in each fork test.
7. `waitForTimeout` used as synchronisation.
8. Duplicating the reporter config between a local script and the config file.
9. Adding a second E2E framework beside Playwright.
10. Treating "the report opens locally" as proof that CI retrieval works.
11. Assuming `ubuntu-latest` is compatible with the pinned Playwright version.
12. Letting a shard die before Playwright runs without a guard in the report job.
13. Setting an environment variable the application does not actually read.

### 7.1 Field lessons from adopters

These were found the hard way by repos that completed this alignment. Each one
produced a red CI run that looked like a test failure but was not.

| Symptom | Real cause | Fix |
| --- | --- | --- |
| `Playwright does not support chromium on ubuntu26.04-x64` | `ubuntu-latest` migrated to 26.04; the pinned Playwright predates it | Pin `runs-on` to an image the Playwright version supports, or bump Playwright |
| `npm ci` fails on a clean runner | `package.json` pinned an exact version while `package-lock.json` resolved a different one | Keep the caret range so `npm ci` matches the lock file |
| Report job fails with a bare "Directory does not exist" | A shard died before Playwright ran, so no blob was uploaded | Guard the merge step and fail with the real cause |
| `global-setup` throws `500` and the report has zero tests | The workflow set an env var the API never reads | Set the config key the app actually binds, and assert it in a health check |
| Specs pass alone but fail in parallel | A global rate limiter is exhausted by concurrent workers | Make the limits configurable with unchanged production defaults |
| A `callCount`-based mock never matches | The page renders twice on boot (a route redirect remounts it) | Use a flag flipped by the user action, not a call count |

---

## §8 Reference inventory (`AGUIWebChat`, verified 2026-11-03)

### 8.1 Layer A as implemented

| Item | Value |
| --- | --- |
| Playwright version | `@playwright/test` 1.64.1 |
| `testDir` | `./e2e/tests` |
| Suite size | 126 tests across 13 spec files |
| Specs | `blocks`, `cancellation`, `chat`, `home`, `models`, `runtime-bootstrap`, `scenario-readiness`, `scenario-recovery`, `scenarios`, `telemetry-settings`, `transcript`, `transport`, `typed-checkpoint-recovery` |
| Local reporters | `[['html', { open: 'on-failure' }], ['list']]` |
| CI reporters | `[['blob'], ['github']]` |
| Retries / workers | `CI ? 1 : 0` / `CI ? 2 : undefined` |
| Servers | Server `http://127.0.0.1:5199`, Client `http://127.0.0.1:5299` |
| Determinism flags | `E2E__InMemoryDatabase=true`, `E2E__StubInference=true`, unreachable `OLLAMA_ENDPOINT` |
| `gracefulShutdown` | `SIGTERM`, 5s |
| Readiness budget | `expect.timeout` 15s, boots 180s, WASM proof 60s |
| Fixture exports | `test`, `expect`, `backend`, `seededModel`, `fork`, `fluentButton`, `clickFluentButton`, `resetScenario`, `waitForInteractive`, `waitForWebAssembly` |
| CI shards | `[1, 2] / 2` |
| CI artifact | `playwright-report`, `retention-days: 90`, `if: always()` |

Repo-local adapters worth copying as *patterns* (not as content): the stub echoes
`E2E echo: <last user message>`; the script endpoints exist only when both
`E2E:StubInference` and `E2E:InMemoryDatabase` are on; WebAssembly is proven by real
`RendererInfo.Name` plus `OperatingSystem.IsBrowser()`, never by elapsed time or theme
mutation.

### 8.2 Layer B as implemented

| File | Current E2E-relevant content |
| --- | --- |
| `.squad/team.md` | Tester row: `Zoe` — `Tester` — `✅ Active` |
| `.squad/routing.md` | `Tests and coverage` → `Zoe` |
| `.squad/agents/zoe/charter.md` | Owns xUnit v3 suites and `E2E/Playwright checks, edge cases, regression gating` |
| `.squad/ceremonies.md` | Failure ceremony condition includes `test failure` |
| `.squad/decisions.md` | **No E2E decision recorded** |
| `.squad/config.json` | `{ "version": 1 }` — default local state backend |

This is the correct shape: assignment in Squad, policy in layer A/C.

### 8.3 Layer C as consumed

Plugin `playwright-report-retrieval` v0.1.0, marketplace `corven-agent-tools` v0.8.0,
`DEFAULT_ARTIFACT_NAME = "playwright-report"`. MCP tools loaded in-session:
`fetch_report`, `list_failed_tests`.

### 8.4 Drift found and corrected during this alignment

`.github/copilot/settings.json` listed `github-issues`, `use-decision-model`, and
`squad-model-resolver` but **not** `playwright-report-retrieval`, while `docs/E2E.md`
instructed readers to use it. Fixed by adding
`"playwright-report-retrieval@corven-agent-tools": true` (commit `1591331`).

### 8.5 Known residual duplication

`docs/E2E.md` still restates the coverage gate, determinism rules, and artifact table
that the plugin skill also defines. §2.7 marks this as a restatement to trim toward a
pointer on the next documentation pass; it is recorded here rather than changed
silently.
