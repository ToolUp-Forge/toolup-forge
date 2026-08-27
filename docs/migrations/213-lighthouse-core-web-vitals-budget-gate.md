# Lighthouse / Core-Web-Vitals budget gate (Phase 213)

**What changes.** `ToolUp.Platform.Build` gains a declarative Core-Web-Vitals budget gate: a budget
file schema, a Lighthouse-report reader, a pure check between them, and a `VerifyCoreWebVitalsBudget`
FAKE target. `dev-scripts/cwv-budget-gate.ps1` drives the measuring half — build a public-rendering
site, serve it on a throwaway port, run Lighthouse over the budgeted page set, sample the
conditional-GET split, and hand everything to the target. A default budget ships at
`samples/PublicSite/cwv-budget.json`.

**Scope.** Additive, and CI/test-only. Nothing is composed into a deployment: no middleware, no
hosted service, no DI registration, no allocation. `ToolUp.PublicRendering` is untouched — SSR output
is byte-for-byte identical whether or not the gate runs (GP 13). A repo that never registers the
target or runs the script sees no change at all.

## What the gate asserts

Per budgeted page, from the Lighthouse JSON report:

| Budget key | Lighthouse audit / category | Direction |
|---|---|---|
| `largestContentfulPaintMs` | `largest-contentful-paint` | ceiling (ms) |
| `cumulativeLayoutShift` | `cumulative-layout-shift` | ceiling (unitless) |
| `totalBlockingTimeMs` | `total-blocking-time` | ceiling (ms) |
| `firstContentfulPaintMs` | `first-contentful-paint` | ceiling (ms) |
| `performance` | `performance` category score | floor (0.0–1.0) |
| `seo` | `seo` category score | floor (0.0–1.0) |
| `accessibility` | `accessibility` category score | floor (0.0–1.0) |
| `bestPractices` | `best-practices` category score | floor (0.0–1.0) |

Plus the optional server-side companion signal, cross-checked from a counter snapshot covering the
same run — see [Server-side cross-check](#server-side-cross-check) below.

**Nothing passes silently.** A budget line the report never measured, and a budgeted page no report
covered, are **breaches**, not skipped lines. An audit that quietly stopped being emitted is
otherwise indistinguishable from one that passed, which is the exact regression class this gate
exists to catch. A reports directory with no reports in it fails the run rather than reading as a
clean sweep.

## The budget file

```json
{
  "schema": "toolup.cwv-budget/v1",
  "label": "PublicSite (default budget)",
  "pages": [ "/", "/about", "/pricing", "/faq", "/news/2026-05-22-launch" ],
  "metrics": {
    "largestContentfulPaintMs": 2500,
    "cumulativeLayoutShift": 0.1,
    "totalBlockingTimeMs": 200,
    "firstContentfulPaintMs": 1800
  },
  "categories": {
    "performance": 0.9,
    "seo": 0.95,
    "accessibility": 0.9,
    "bestPractices": 0.9
  },
  "serverSignals": {
    "minConditionalGet304Rate": 0.45,
    "required": false
  }
}
```

`pages` is the single page list: the runner measures exactly what the budget asserts, so the two
cannot drift apart. Paths are compared without host or trailing slash, so a budget never mentions the
throwaway port a run happened to draw.

The parser refuses a malformed file with **every** defect named in one run — a missing `schema`, an
absent or empty `pages`, a typo'd metric key, a category floor written as `95` rather than `0.95`, a
duplicated page, an unknown `serverSignals` key. It also refuses the most dangerous shape of all: a
syntactically perfect budget that asserts no thresholds and therefore can never fail.

## Running it locally

Needs Node (for `npx lighthouse`) and a Chromium-family browser. Lighthouse resolves its browser via
`CHROME_PATH` when Chrome is not on the default install path — on a machine carrying only Edge, point
it at `msedge.exe` first.

```powershell
pwsh ./dev-scripts/cwv-budget-gate.ps1
```

The script builds the site, draws a free ephemeral port (never `5040`, `6000` or `7680`), serves the
sample on it via `SERVER_PORT`, probes the conditional-GET split, runs Lighthouse per page, stops the
server, and evaluates. Non-zero exit on any breach, with each breach named and both numbers printed.

Useful switches:

| Switch | Effect |
|---|---|
| `-Budget <path>` | check a different budget (and therefore a different page set) |
| `-Site <fsproj>` | serve a different public-rendering site |
| `-ReportsDirectory <dir>` | where reports are written (default `artifacts/cwv-reports`) |
| `-SkipBuild` | reuse an existing build of the site |
| `-EvaluateOnly` | skip build + serve + browser entirely; evaluate reports already present |
| `-ServerMetrics <path>` | cross-check against a specific counter snapshot |
| `-Port <n>` | pin the served port instead of drawing one (for a constrained CI network) |

`-EvaluateOnly` is the fast inner loop while editing a budget — no build, no server, no browser:

```powershell
pwsh ./dev-scripts/cwv-budget-gate.ps1 -EvaluateOnly `
    -Budget src/ToolUp.Platform.Build.Tests/fixtures/cwv/fixture-budget.json `
    -ReportsDirectory src/ToolUp.Platform.Build.Tests/fixtures/cwv/within-budget `
    -ServerMetrics src/ToolUp.Platform.Build.Tests/fixtures/cwv/server-metrics.json
```

Swap `within-budget` for `breaching` to watch the gate go red against the deliberately-degraded
fixture set — worth doing once, so you have seen the gate fail before you trust it passing.

The deciding half is reachable on its own, which is what CI invokes when the reports were produced by
some other means:

```text
TOOLUP_CWV_BUDGET=samples/PublicSite/cwv-budget.json
TOOLUP_CWV_REPORTS=artifacts/cwv-reports
TOOLUP_CWV_SERVER_METRICS=artifacts/cwv-reports/server-metrics.json   (optional)
dotnet run --project Build.fsproj -- VerifyCoreWebVitalsBudget
```

## Running it in CI

The gate is opt-in: it is not wired into `checks.yml`, because a browser-driven job is slow, needs a
Chromium install on the runner, and — measuring a real browser on shared hardware — is inherently
noisier than the rest of the suite. Enable it deliberately, as its own job, on the cadence that suits
the deployment (per-PR for a site whose performance is the product; nightly or pre-release otherwise):

```yaml
  cwv-budget:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      - uses: actions/setup-node@v4
        with:
          node-version: '22'
      # ubuntu-latest ships Chrome; on a bare runner install it and export
      # CHROME_PATH before this step.
      - name: Core-Web-Vitals budget
        shell: pwsh
        run: pwsh ./dev-scripts/cwv-budget-gate.ps1
      - name: Publish the reports
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: cwv-reports
          path: artifacts/cwv-reports
```

Upload the reports on `always()`, not on success: the run you most want the JSON from is the one that
failed.

**Prove the job red before trusting it green.** Point it at the breaching fixture set on a scratch
branch (`-EvaluateOnly -ReportsDirectory src/ToolUp.Platform.Build.Tests/fixtures/cwv/breaching`) and
confirm the job fails. An unproven gate is precisely the failure mode a gate exists to prevent.

## Widening a budget

**Every threshold lives in the committed budget file, and that is the only way to move one.** There
is no inline override, no environment-variable escape, and no "warn instead of fail" mode. Widening a
budget is therefore a diff with an author, a reviewer, and a reason:

```diff
   "metrics": {
-    "largestContentfulPaintMs": 2500,
+    "largestContentfulPaintMs": 3200,
```

That is the whole mechanism, and it is deliberate. A gate whose thresholds can be relaxed at the call
site records nothing; a gate whose thresholds live in a file records who relaxed them and when. Put
the reason in the commit message — the budget file is JSON and cannot carry a comment.

The same applies to dropping a page from `pages`, which is the quieter way to widen a budget: the
page stops being measured, and nothing else changes. Both edits are visible in review; neither is
visible in a log.

## Server-side cross-check

The gate reads a browser-side measurement, but the public-rendering tier already emits two cheap
server-side signals worth checking alongside it, and the budget can assert on both:

- **`minConditionalGet304Rate`** — a floor on the 304 share of the conditional-GET counter's two
  outcomes. The runner samples this from the wire: one cold GET per page, then one revalidation
  carrying the `ETag` and `Last-Modified` the cold response returned. That mirrors what the server's
  own counter records, since it tags every rendered response `200` and every not-modified response
  `304`. A collapsed rate means revalidating crawlers are being served whole pages they already hold
  — a crawl-budget regression a Lighthouse score will never show you.
- **`maxRenderMs`** — a ceiling on the worst server-side render duration. The runner does **not**
  sample this, deliberately: an HTTP round-trip is not the server's render time, and recording one as
  the other would be a fabricated signal. A deployment that exposes its metrics sink writes
  `renderMsMax` into the snapshot itself, and this ceiling then bites.

The snapshot is plain JSON, accepted in either the short form the runner writes or a verbatim
transcription of the counter names, so nothing has to be translated:

```json
{ "conditionalGet": { "304": 5, "200": 5 } }
```

```json
{
  "publicrendering.render_ms": { "max": 12.5 },
  "publicrendering.conditional_get": { "304": 3, "200": 1 }
}
```

`serverSignals.required` decides what an **unsampled** signal means. Left `false` (the default), a run
that supplies no snapshot reports the omission as an advisory line and passes — so the gate stays
runnable where no metrics surface is reachable, while the omission is still visible in the report. Set
`true`, and an unsampled signal fails the run.

## Verification

1. `dotnet build ToolUp.Forge.sln` clean.
2. `dotnet run --project Build.fsproj -- VerifyAll` — the `Build` pack carries the gate's test pack:
   the parser's refusals, the check over committed report fixtures, and the server-signal arms.
3. `pwsh ./dev-scripts/cwv-budget-gate.ps1 -EvaluateOnly …` against the `within-budget` fixture set
   exits 0; against `breaching` it exits non-zero and names each breach.
4. Full run: `pwsh ./dev-scripts/cwv-budget-gate.ps1` — the shipped sample site passes under the
   default budget.

## Consumer adoption

None required. This ships no consumer-visible surface change: nothing is composed, nothing is
registered, and no existing behaviour moves. Adopting the gate is opting into it — copy
`samples/PublicSite/cwv-budget.json` next to your own site, edit its `pages` and thresholds, and run
the script. A consumer repo that wants the target in its own `Build.fs` registers it directly:

```text
CoreWebVitalsBudgetGate.registerTarget ()
```

Registration reads no environment at startup, so every other target in that repo stays runnable with
none of the gate's variables set.

## See also

- `src/ToolUp.Platform.Build/Build/SDK.CoreWebVitalsBudget.fs` — the budget parser, report reader and
  check.
- `src/ToolUp.Platform.Build.Tests/CoreWebVitalsBudgetTests.fs` — the contract pack.
- `src/ToolUp.Platform.Build.Tests/fixtures/cwv/` — the committed Lighthouse report fixtures.
- `dev-scripts/cwv-budget-gate.ps1` — the runner.
- [`static-export-public-rendering.md`](static-export-public-rendering.md) — the static-export
  terminus, an alternative thing to point the gate at.
