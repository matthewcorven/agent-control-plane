# Handoff — fix `playwright-report-retrieval` retrieval defects

**Audience:** the coding agent working in **`matthewcorven/corven-agent-tools`**.
**Author:** Squad (Coordinator, AGUIWebChat). **Date:** 2026-11-03.
**Status:** consumer-side confirmation complete; fixes belong in your repo, not in the consumer.
DEF-1 through DEF-6 were fixed in `0.2.0`; DEF-7 in `0.2.1`; DEF-8 in `0.2.2`; DEF-9 in `0.2.3` (§2b).

You are receiving this because a real consumer repo (`matthewcorven/AGUIWebChat`) tried to use the
`playwright-report-retrieval` plugin for its documented primary job — fetch the newest CI Playwright
HTML report — and it failed in **six** separate ways. Nothing below is speculative: every error
string and every wrong result was produced by the shipped code against a live GitHub Actions run
and the artifact it produced.

Do not "fix" this from the consumer side. `AGUIWebChat/docs/AGENT-PROMPT-PLAYWRIGHT-ALIGNMENT.md` §4.2
explicitly forbids target repos from patching the plugin, so the consumer deliberately fell back to a
raw `gh run download` and is currently blocked on you.

The most serious finding is **DEF-4**: the plugin's headline tool reports *zero* failures against a
report that contains 126 tests. It does not error — it silently returns a clean bill of health. That
is worse than a crash, and it is why this handoff exists.

---

## 1. Reproduction environment

| Fact | Value |
| --- | --- |
| Consumer repo | `matthewcorven/AGUIWebChat` (public) |
| Workflow | `.github/workflows/e2e.yml` (sharded Playwright, 2 shards, `merge-reports`) |
| Failing run | `41582240170` — status `success`, headSha `c80379216b41b966667276d50e30c31eda3046a7` |
| Artifact | `playwright-report`, 653,921 bytes, `expired=false` |
| Extracted artifact contents | **`index.html` only**, 1,138,544 bytes. No `data/` directory. |
| Playwright version | `@playwright/test` 1.64.1 |
| `gh` version | 2.102.1 (2026-10-20), authenticated, macOS |
| Plugin install mode on the reporter's machine | **direct/local** (`~/.copilot/config.json` → `"marketplace": ""`, `source.source = "local"`), not a marketplace install |
| Plugin source | `plugins/playwright-report-retrieval` @ `9c2bd66`, version `0.1.0` |

The artifact is fine. This command proves it:

```shell
gh api repos/matthewcorven/AGUIWebChat/actions/runs/41582240170/artifacts \
  --jq '.artifacts[] | "\(.name) \(.size_in_bytes) expired=\(.expired)"'
# playwright-report 653921 expired=false
```

And this command proves the payload is real and complete:

```shell
gh run download 41582240170 --repo matthewcorven/AGUIWebChat \
  --name playwright-report --dir /tmp/pwprobe
ls -l /tmp/pwprobe
# total 1136
# -rw-r--r--  1 core staff 1138544 index.html
```

---

## 2. The six defects

### DEF-1 — `gh` is invoked from a cwd that is not a git repository

**Symptom** (MCP `fetch_report` with only `run_id`, exactly as the tool description invites):

```
gh api /repos/{owner}/{repo}/actions/runs/41582240170/artifacts failed:
unable to expand placeholder in path: failed to run git: fatal: not a git repository
```

**Root cause:** `GitHubActionsProvider._api()` (`scripts/fetch_report.py:118`) calls `gh api` on
endpoints containing the `{owner}`/`{repo}` placeholder — `resolve_run_id()` at line **123** and
`find_artifact()` at line **146**. `gh` resolves those placeholders only from the current working
directory's git remote or from the `GH_REPO` environment variable. The MCP server's cwd is wherever
the host launched `python3 .../server.py` — in the failing case, outside any repo.

**Blast radius:** not just artifact lookup. `resolve_run_id()` is equally affected, so `--commit`
resolution is broken in the same environment. The failure mode depends on process cwd, which makes
it non-deterministic across hosts — the worst kind of bug for an installed tool.

**Verified fix mechanism:** setting `GH_REPO=matthewcorven/AGUIWebChat` in the environment makes the
same `gh api` call succeed from `/tmp`. Use that, or expand the placeholder yourself from the `repo`
argument before the call. Either is fine; do not leave placeholder expansion to `gh`'s cwd.

---

### DEF-2 — the `repo` parameter is passed as `--repo` to `gh api`

**Symptom:**

```
gh api /repos/{owner}/{repo}/actions/runs/41582240170/artifacts --repo matthewcorven/AGUIWebChat failed:
unknown flag: --repo
```

**Root cause:** `_gh()` (`scripts/fetch_report.py:95`) appends `--repo <repo>` unconditionally to
every invocation. `gh api` has no `--repo` flag. (It also has no `-L`/`--follow`; check before you
add any other global flags to that path.)

**Fix:** only append `--repo` for subcommands that accept it (`gh run …`, `gh pr …`). For `gh api`,
pass the repo through `GH_REPO` (see DEF-1) or inline it into the path.

**Note:** DEF-1 and DEF-2 mask each other. With no `repo` argument you get DEF-1; with a `repo`
argument you get DEF-2. Both must be fixed together or neither call site works.

---

### DEF-3 — the download/extract contract contradicts itself (the hard blocker)

**Symptom** (MCP `fetch_report` with `run_id` **and** `repo`, after working around DEF-1/DEF-2):

```
The downloaded artifact 'playwright-report' contained no .zip archive.
```

**Root cause:** `GitHubActionsProvider.download_artifact()`
(`scripts/fetch_report.py:171`) shells out to `gh run download`, which **auto-extracts** the artifact
into the destination directory. It never leaves a `.zip` behind. But `fetch_report()`
(`scripts/fetch_report.py:334`) then does:

```python
archives = sorted(staging.rglob("*.zip"))   # line 405
if not archives:
    raise FetchError(
        f"The downloaded artifact '{artifact_name}' contained no .zip archive."  # line 407
    )
```

The provider's own return contract and the consumer's expectation are incompatible. The tool can
never succeed on a real artifact, regardless of network, auth, or run ID.

**Fix:** treat the provider result as a **report root**, not an archive. `extract_zip()` already
contains exactly the right root-detection logic at lines 255–262 (walk `rglob("index.html")`, prefer
the shallowest match). Factor that into a `_find_report_root(path) -> Path` helper and call it from
both `extract_zip()` and `fetch_report()`. Keep zip support — users may pass a local archive — but
the download path must not require one.

---

### DEF-4 — `list_failed_tests` silently returns zero failures for every real report ⚠️

**Symptom** — run against the extracted artifact from §1, which contains 126 tests, all passing:

```python
list_failed_tests(report_dir="/tmp/pwprobe")
# {'total': 0, 'failed_count': 0, 'failed': []}
```

`total: 0` is the tell. The report has **126** tests. The tool cannot see any of them.

**Root cause, two independent bugs stacked:**

1. **`_read_report_data()` (`server.py:117`) looks for files that no longer exist.** It probes
   `data/report.json` and `data/index.json` (line 126) and then root-level `*.json`. Modern
   `@playwright/test` (≥1.3x) emits a **single self-contained `index.html`** with the entire payload
   embedded as a base64 zip:

   ```html
   <template id="playwrightReportBase64">data:application/zip;base64,UEsDBBQ…</template>
   ```

   There is no `data/` directory and no sibling JSON. So `_read_report_data()` returns an empty
   structure and `list_failed_tests()` reports zero tests — **no error, no warning**.

2. **`_walk_suites()` (`server.py:140`) walks a shape that does not exist.** Line 201 does
   `data.get("suites")`. The decoded payload has **no `suites` key**. It is 14 entries of
   `{fileId, fileName, tests: [...]}` — a flat per-file array, one file per test spec:

   ```
   decoded zip: 466,287 bytes, 14 entries, all <fileId>.json
   top keys: ['fileId', 'fileName', 'tests']
   ```

   Per-test fields actually present: `testId`, `title`, `projectName`,
   `location{file,line,column}`, `duration`, `annotations`, `tags`, `outcome`
   (`expected` | `unexpected` | `flaky` | `skipped`), `path`, `results[]`, `ok`.
   Per-attempt status lives at `results[*].status`
   (`passed` | `failed` | `timedOut` | `interrupted` | `skipped`).

3. **Status precedence bug at `server.py:156`:**

   ```python
   "status": test.get("status") or spec.get("ok") and "passed" or "unknown",
   ```

   Python binds `and` tighter than `or`, so this is
   `test.get("status") or (spec.get("ok") and "passed") or "unknown"`. It can never yield
   `"failed"`, and it reports `"unknown"` for anything not `ok`-truthy. Even with a correct tree walk,
   this line would mislabel results.

**Fix:** add a real reader for the current format.

1. In `_read_report_data()`, detect `index.html`, extract the `<template id="playwrightReportBase64">`
   payload, strip the `data:application/zip;base64,` prefix, **pad before decoding**
   (`b64 + "=" * (-len(b64) % 4)`) — Playwright's base64 is unpadded and a naive
   `base64.b64decode()` raises
   `Invalid base64-encoded string: number of data characters (621741) cannot be 1 more than a multiple of 4`.
2. Unzip in memory with `zipfile.ZipFile(io.BytesIO(...))` and `json.loads` each `<fileId>.json`.
3. Derive status from `outcome` / `results[*].status` — never from `spec.get("ok")`.
4. Keep the legacy `suites` walk as a fallback so older reports still work, but make the modern
   format the primary path.
5. `total` must be the number of tests seen, not the number of failures.

**Acceptance signal:** `total: 126`, `failed_count: 0` — not `total: 0`.

---

### DEF-5 — the plugin's test suite is not in CI

**Symptom:** `.github/workflows/test.yml`, job `unit`, runs only:

- `github-issues/skills/issue-work-queue/tests`
- `squad-model-resolver/tests`

`plugins/playwright-report-retrieval/tests` is never executed. The plugin has tests; CI ignores them.

**Why this let DEF-3 and DEF-4 ship:** both test fakes encode the *wrong* shapes, so they pass:

- `tests/test_fetch_report.py:127` — `make_zip(destination / "report.zip", {"index.html": "<html></html>"})`.
  The fake writes a zip; real `gh run download` does not. This masks DEF-3.
- `tests/test_mcp_smoke.py:75` — writes a synthetic `data/report.json` containing a `suites` tree.
  No real report has that file. This masks DEF-4.

**Fix:** add the plugin's tests to the `unit` job **and** re-point both fakes at the real shapes
(see §3.4). Adding the job without fixing the fakes just locks in the false green.

---

### DEF-6 — the cache namespace is written into the plugin source tree

**Symptom:** `default_namespace()` (`scripts/fetch_report.py:204`) returns
`Path.cwd() / "playwright-reports"`. When the MCP host launches the server with cwd set to the
plugin directory, every fetch writes into the plugin's own source tree. `.gitignore` in
`corven-agent-tools` has no `playwright-reports/` entry, so the cache shows up as untracked noise in
`git status` and can be committed by accident.

**Fix:** resolve the namespace relative to the **target repo root** when one can be determined, and
add `playwright-reports/` to `.gitignore` as a belt-and-braces guard.

---

## 2b. Defects found after 0.2.0 shipped

These two were found while validating `0.2.0` against a second consumer repo
(`matthewcorven/omlx-manager`). They are recorded here because this file is the canonical defect
ledger, not because the original handoff was wrong — DEF-7 is a defect the first pass did not reach,
and DEF-8 is a defect the first pass *introduced the conditions for*.

### DEF-7 — `--workflow` is silently ignored when resolving a run by commit

**Status:** fixed in `0.2.1` (commit `cbcf401`). Validated at CLI and MCP layers.

**Symptom:** `fetch_report(commit=<sha>, repo=…, workflow="e2e.yml")` resolved the wrong run. On
`matthewcorven/omlx-manager` commit `ae24b42` it picked run `41703114625` ("Squad Release") instead
of `41703114762` ("E2E (Playwright)"), then failed with
`Artifact 'playwright-report' not found on run 41703114625. Available artifacts: none.`

**Root cause:** `scripts/fetch_report.py:214-215` appended `&workflow_id={workflow}` to
`GET /repos/{owner}/{repo}/actions/runs`. **`workflow_id` is not a valid filter on that endpoint.**
GitHub accepts it and ignores it, so the filter was a no-op and every run for the SHA came back.
`resolve_run_id()` then took `completed[0]` — the first completed run in GitHub's order, which is not
guaranteed to be the one you asked for.

This is the same failure class as DEF-2: a plausible-looking flag that the API ignores rather than
rejects. Both produce a confident wrong answer instead of an error.

**Fix:** when `workflow` is set, use the workflow-scoped endpoint
`/repos/{repo}/actions/workflows/{workflow}/runs?head_sha=…` (the `{workflow}` segment accepts a file
name or a numeric id; URL-encode it). Without `workflow`, walk the completed runs and prefer the first
one whose artifact list contains the requested artifact, falling back to the first completed run.

**Why it matters beyond this repo:** any repository with more than one workflow per commit could
silently resolve the wrong run. The failure is intermittent and data-dependent, which is the worst
kind — it works until the day a second workflow happens to finish first.

### DEF-8 — `list_failed_tests` crashes when the server's cwd has been deleted

**Status:** fixed in `0.2.2` (commit `40456c6`). Validated at the Python and MCP layers.

**Symptom:** every MCP call to `list_failed_tests` returns `{"error": "[Errno 2] No such file or
directory"}` — no traceback, no path, no hint. `fetch_report` works in the same session.

**Root cause:** `scripts/fetch_report.py:345`:

```python
return (_git_toplevel() or Path.cwd()) / DEFAULT_NAMESPACE
```

`_git_toplevel()` is correctly defensive and returns `None` on `OSError`. The `or` fallback then
calls `Path.cwd()`, which raises `FileNotFoundError` when the process's working directory no longer
exists. `list_failed_tests` omits `namespace`, so it always reaches `default_namespace()`.

**Why the cwd is gone:** MCP servers are long-lived. The ones in this environment were started with
cwd `/Users/core/.copilot/.playwright-report-retrieval.bak-18283-0`, a temp directory that
`copilot plugin update` created and then deleted. The server then fails on every call that needs a
default namespace, for the rest of its life.

**Fix:** make `default_namespace()` total — never let cwd resolution raise. Fall back through
`_git_toplevel()` → a guarded `Path.cwd()` → `tempfile.gettempdir()`. Also replace the bare errno
with an actionable message naming the fix (pass an explicit `namespace`).

**Operational workaround until fixed:** pass `namespace` explicitly to `list_failed_tests`, or
restart the CLI so the server is respawned in a live directory.

---

### DEF-9 — a report with zero tests and global errors is reported as a clean pass

**Status:** fixed in `0.2.3` (commit `f573043`). Validated at the Python layer.

**Symptom:** `list_failed_tests` returns `total: 0, failed_count: 0` for a run that executed **no
tests at all** and aborted in `global-setup`. That is indistinguishable from a genuinely clean run,
so a consumer that gates on `failed_count == 0` will happily pass a run that never ran anything.

**Reproduction fixture:** a real `playwright-report` artifact from a Playwright **1.57.0** run whose
`global-setup` threw. Decoding the embedded payload yields a single `report.json` with
`files: []`, `stats: {total: 0, ok: true}`, and `errors: [<two identical global-setup failures>]`.

**Root cause A — the embedded-report regex is `<template>`-only.** `_decode_embedded_report()`
matched:

```python
r'<template[^>]*id="playwrightReportBase64"[^>]*>(.*?)</template>'
```

Playwright **1.64.x** emits `<template id="playwrightReportBase64">`. Playwright **1.57.x** emits
`<script id="playwrightReportBase64" type="application/zip">`, and places it **after `</html>`**.
The regex therefore found nothing and returned `{}`, which the caller treated as "no failures".
The plugin was silently coupled to one Playwright version's HTML shape.

**Root cause B — `errors[]` was never surfaced.** Even with a correct decode, `_read_report_data()`
→ `_collect_tests()` only understood the per-file shape (`files[]` → `tests[]`). A `report.json`
with an empty `files[]` and a populated `errors[]` produced `total: 0, failed_count: 0`. Note that
Playwright itself sets `stats.ok: true` for a global-setup abort, so `errors[]` is the *only*
signal that anything went wrong.

**Fix:** match either tag shape anywhere in the document, anchored on a real tag (the bundled JS
also contains incidental `playwrightReportBase64` string literals that a bare id search would
false-match); normalize the single-`report.json`-with-inline-`files[]` shape alongside the
per-file shape; count each `errors[]` entry in `failed_count`, return them as `errors` with an
`error_count`, and append a synthetic failed record to `failed[]` so consumers that read only
`failed[]` still see it. A zero-test, zero-error report returns a `warning` instead of raising.

**Consumer rule:** never treat `total: 0` as success. Require a non-zero `total` **and**
`failed_count: 0`. A `warning` or a non-empty `errors` array is a failure regardless of
`failed_count`.

---

## 3. Required fix

Work in `plugins/playwright-report-retrieval`. Keep the change surgical — this is a retrieval
correctness fix, not a redesign.

### 3.1 Repo resolution (DEF-1, DEF-2)

- Thread `repo` through as an explicit value; never rely on `gh`'s cwd inference.
- For `gh api` calls, set `GH_REPO` in the subprocess environment (or inline the slug into the path).
- In `_gh()`, gate the `--repo` flag on the subcommand.
- `repo` must remain **optional** in the MCP schema — when omitted, resolve from cwd and produce a
  clear, actionable error if that fails, rather than surfacing `gh`'s git error verbatim.

### 3.2 Download contract (DEF-3)

- `download_artifact()` keeps returning the extracted directory.
- `fetch_report()` stops looking for `*.zip`; it calls `_find_report_root(staging)`.
- `_find_report_root()` is the single source of truth, shared with `extract_zip()`.
- Error message when no root is found must name the directory searched and what it expected
  (`index.html`).

### 3.3 Read the real HTML report (DEF-4)

- New reader for `<template id="playwrightReportBase64">` → base64 → zip → per-file JSON.
- Pad base64 before decoding.
- Status derived from `outcome` and `results[*].status`; delete the `spec.get("ok")` expression.
- `total` counts tests seen.
- Legacy `suites` walk retained as a fallback path.

### 3.4 Re-point the fakes and add regression tests

Both existing fakes are wrong and must be replaced, not merely supplemented:

- `tests/test_fetch_report.py` — the fake provider must write an **extracted `index.html`**, matching
  `gh run download`'s actual behavior. Add a case that still covers a genuine `.zip` input.
- `tests/test_mcp_smoke.py` — replace the synthetic `data/report.json` fixture with a **real
  `index.html`** carrying an embedded base64 zip, in the real shape (`{fileId, fileName, tests[]}`).

New regression tests required:

1. Unpadded base64 decodes without raising.
2. `total` equals the number of tests in the fixture (assert a non-zero count).
3. A fixture with one `outcome: "unexpected"` test yields exactly one failure, with the right
   `testId`, `title`, and file.
4. A fixture with only `expected` tests yields `failed_count: 0` but **non-zero `total`** — this is
   the DEF-4 regression guard, and it is the single most important test in this handoff.
5. `fetch_report()` succeeds when the provider returns an extracted directory with no zip.
6. `_gh()` does not emit `--repo` for `gh api`.

### 3.5 Close DEF-5 and DEF-6

- Add `plugins/playwright-report-retrieval/tests` to the `unit` job in `.github/workflows/test.yml`.
- Add `playwright-reports/` to `.gitignore`.
- Fix `default_namespace()` to resolve against the target repo root.

### 3.6 Version and docs

- Bump `plugin.json` `version` to `0.2.0` and update the matching entry in
  `.github/plugin/marketplace.json`.
- Update `skills/playwright-report-retrieval/SKILL.md` where it describes report layout
  (lines ~118, ~170, ~185): document the self-contained `index.html` format and the fact that
  `playwright-reports/` is a cache directory, not a source artifact.

---

## 4. Acceptance criteria

All of these must pass. The first one is the reason for the handoff.

```shell
# 1. Real report, real counts. MUST print total=126 failed=0 (NOT total=0).
gh run download 41582240170 --repo matthewcorven/AGUIWebChat \
  --name playwright-report --dir /tmp/pwaccept
python3 -c "
import json,sys; sys.path.insert(0,'plugins/playwright-report-retrieval')
from server import list_failed_tests
r = list_failed_tests(report_dir='/tmp/pwaccept')
print('total=%d failed=%d' % (r['total'], r['failed_count']))
assert r['total'] > 0, 'DEF-4 regression: tool cannot see tests in a real report'
"

# 2. Runs from outside any git repo, with an explicit repo argument (DEF-1 + DEF-2).
#    Note the CLI flag is --namespace (not --output); --no-open keeps it headless.
cd /tmp && python3 /Users/core/git/matthewcorven/corven-agent-tools/plugins/playwright-report-retrieval/scripts/fetch_report.py \
  --run-id 41582240170 --repo matthewcorven/AGUIWebChat \
  --namespace /tmp/pwaccept2 --no-open --json

# 3. Runs from outside any git repo, with no repo argument (DEF-1 only) — must fail with a
#    clear, actionable message, never with `gh`'s `fatal: not a git repository` verbatim.
cd /tmp && python3 /Users/core/git/matthewcorven/corven-agent-tools/plugins/playwright-report-retrieval/scripts/fetch_report.py \
  --run-id 41582240170 --namespace /tmp/pwaccept3 --no-open --json ; echo "exit=$?"

# 4. A known failing report yields the right failure (use any run whose report is red).
python3 -c "
import sys; sys.path.insert(0,'plugins/playwright-report-retrieval')
from server import list_failed_tests
r = list_failed_tests(report_dir='/tmp/pwred')
assert r['failed_count'] == 1 and r['failed'][0]['test_id'], r
"

# 5. Unit tests pass, and CI actually runs them.
python3 -m pytest plugins/playwright-report-retrieval/tests -q
grep -q 'playwright-report-retrieval' .github/workflows/test.yml && echo 'DEF-5 closed'
grep -q 'playwright-reports/' .gitignore && echo 'DEF-6 closed'

# 6. No stray cache in the source tree after a fetch.
git status --porcelain | grep -q 'playwright-reports' && echo 'FAIL: cache leaked' || echo 'clean'
```

Known-good reference values for criterion 1:

- Report: 14 spec files, **126 tests**, all `outcome: "expected"`.
- A real `testId` you can assert on in a red-report fixture:
  `64561075f5a2a62f395c-940aa7fff51615807aba`
  — title `tool continuation cancellation: Server Stop preserves execution and a live renderer`,
  file `cancellation.spec.ts`.

---

## 5. Repo constraints you must honour

- **Stdlib only.** `corven-agent-tools` ships Python with no third-party dependencies. `zipfile`,
  `base64`, `io`, `json`, `html.parser`/`re` are all fine. Do **not** add a dependency.
- **Agent Plugins 1.0 format.** `plugin.json` and `.github/plugin/marketplace.json` must stay in
  sync on `version`.
- **SKILL.md frontmatter must parse as YAML.** The CI `frontmatter` job enforces this; do not
  introduce tabs or unquoted colons in the frontmatter block.
- **Conventional commits.** One commit per logical change is preferred:
  `fix(playwright-report-retrieval): …`, `test(playwright-report-retrieval): …`,
  `ci(playwright-report-retrieval): …`, `chore(playwright-report-retrieval): bump to 0.2.0`.
- **Tests are the contract.** Do not delete or weaken an existing assertion to make CI green. The
  two fakes identified in DEF-5 are wrong and must be corrected, not removed.

---

## 6. Out of scope

- Rewriting the plugin's MCP schema or renaming tools.
- Adding new providers (GitLab, Azure DevOps, CircleCI).
- Changing the plugin's CLI surface beyond what DEF-1/DEF-2 require.
- Anything in `AGUIWebChat`. The consumer will re-verify after you publish `0.2.0`; do not open a
  PR against it.
- Local (non-CI) Playwright report generation. That is a consumer-repo concern.

---

## 7. Evidence appendix — verbatim failures

Kept verbatim so you can grep them and so nothing is lost in paraphrase.

**DEF-1**

```
gh api /repos/{owner}/{repo}/actions/runs/41582240170/artifacts failed:
unable to expand placeholder in path: failed to run git: fatal: not a git repository
```

**DEF-2**

```
gh api /repos/{owner}/{repo}/actions/runs/41582240170/artifacts --repo matthewcorven/AGUIWebChat failed:
unknown flag: --repo
```

**DEF-3**

```
The downloaded artifact 'playwright-report' contained no .zip archive.
```

**DEF-4**

```
Invalid base64-encoded string: number of data characters (621741) cannot be 1 more than a multiple of 4
```
(naive `b64decode` on the unpadded payload — the first thing a fix will hit)

```
{'total': 0, 'failed_count': 0, 'failed': []}
```
(actual `list_failed_tests` output for a report containing 126 tests — the silent-wrong-answer bug)

**Environment probe**

```
$ gh --version | head -1
gh version 2.102.1 (2026-10-20)

$ ls -l /tmp/pwprobe
-rw-r--r--  1 core staff 1138544 index.html      # no data/, no *.json, no *.zip

$ python3 -c "…decode playwrightReportBase64…"
decoded zip: 466287 bytes, 14 entries
top keys: ['fileId', 'fileName', 'tests']
```

---

## 8. Definition of done

- [ ] DEF-1 through DEF-6 each have a code change and a test or CI check that would have caught it.
- [ ] DEF-7 and DEF-8 (§2b) are closed with regression tests.
- [ ] Acceptance criterion 1 prints `total=126 failed=0`.
- [ ] All six acceptance commands in §4 pass on a clean clone.
- [ ] `plugins/playwright-report-retrieval/tests` runs in the `unit` CI job.
- [ ] `plugin.json` and `marketplace.json` both read the current version.
- [ ] No third-party dependency added; SKILL.md frontmatter still parses.
- [ ] Commit messages are conventional.
- [ ] DEF-7, DEF-8, and DEF-9 (§2b) are closed with regression tests.

---

## 9. Operational lessons for consumers

Three things cost real debugging time while validating `0.2.0`–`0.2.2`. All are consumer-side, not
plugin defects, and all belong in the alignment spec rather than in the plugin.

1. **After `copilot plugin update`, restart the CLI before trusting MCP results.** MCP servers are
   long-lived processes that hold the plugin code in memory. Updating the files on disk does not
   update a running server. During this validation, eight `server.py` processes were still executing
   `0.1.0` code after the on-disk install read `0.2.0`, which made a fixed defect look unfixed. The
   CLI layer (which re-reads from disk) passed while the MCP layer failed — that split is the
   signature of a stale server process.

2. **Pass `repo` explicitly to every MCP call.** The stdio server's cwd is whatever directory the CLI
   happened to spawn it in — the plugin install dir, a temp dir, or (per DEF-8) a directory that no
   longer exists. It is never the target checkout. `list_failed_tests(run_id=…)` without `repo`
   therefore always fails from an MCP client, even when you are sitting inside the repo.

3. **Never accept `total: 0` as a pass.** A report can contain zero tests and still be a failure —
   a `global-setup` abort produces exactly that, and Playwright marks it `stats.ok: true`. Gate on
   `total > 0 && failed_count == 0`, and treat a non-empty `errors` array or a `warning` as a
   failure. Before `0.2.3` this was a silent false-clean (DEF-9); the plugin now surfaces it, but
   the consumer-side assertion is what actually protects the pipeline.

