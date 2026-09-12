# Phase 192 — cold-start / hot-path perf-budget CI gate

**What changes for you: nothing.** This is test-only substrate. No runtime surface was added,
no package gained a type, and every shipped app is byte-for-byte identical whether or not this
gate exists. There is no adoption step. Read on only if you maintain forge, or if this gate has
gone red on your PR.

---

## What it is

A recurring CI guard over two numbers the SDK had asserted once and never re-measured:

| Metric | What it measures | Ceiling | Quiet baseline |
|---|---|---|---|
| `coldStartMs` | process start → the host's `Application started.` line | 6000 ms | 420 ms |
| `hotPathMs` | one anonymous request through the composed pipeline | 5 ms | 0.36 ms |

Plus one non-timing rule: the measured app's build output must contain **no**
`ToolUp.Metrics.OpenTelemetry` assembly. The Phase 9y "default-on DROPPED" conclusion said a
companion nobody registered costs nothing; that is checkable exactly at the byte level, where
"costs no measurable time" is not.

Ceilings live in `perf-budgets.json` at the repo root. Raising one is a reviewable diff.

## Why

Phase 16 (serverless deployment patterns) shipped cold-start *mitigation* and quoted
`Cold start < 2s` as its acceptance criterion — measured once, by hand, at the moment it
shipped. Nothing has measured it since. A budget
asserted once is not a budget; it is a claim with a date on it, and the substrate has accreted
for months underneath it.

## The two halves

The split mirrors the Phase 213 Core-Web-Vitals gate in the same package, deliberately.

| Half | Where | What it owns |
|---|---|---|
| **Measuring** | `dev-scripts/perf-budget-gate.ps1` | build, boots, requests, output inventory |
| **Deciding** | `src/ToolUp.Platform.Build/Build/SDK.PerfBudget.fs` | the budget parser, the check, the `VerifyPerfBudget` FAKE target |

Everything that decides anything is committed F# with a test pack over it
(`ToolUp.Platform.Build.Tests`, the `Build` pack in `VerifyAll`), so a defect in the comparison
is caught by the ordinary suite and needs no process, port or clock. The script owns only the
parts that need a socket.

## Running it

```powershell
pwsh ./dev-scripts/perf-budget-gate.ps1                 # build, measure, decide
pwsh ./dev-scripts/perf-budget-gate.ps1 -EvaluateOnly   # re-decide the last run's numbers
pwsh ./dev-scripts/perf-budget-gate.ps1 -TeethCheck     # …and prove the gate can still fail
```

CI runs the first form with `-TeethCheck`, as its own `perf-budget` job in `checks.yml` —
separate from `verify-all` so wallclock is not measured against eleven other test packs, and so
the ~28-minute critical path does not grow.

## If this gate goes red on your PR

The failure names both numbers. Read which finding it is:

- **`[breach]`** — the measured value exceeded the ceiling. These ceilings are set to catch an
  *order-of-magnitude* regression, so a breach almost always means something in the default path
  started doing real work: a middleware registered unconditionally, an eager singleton that
  touches the disk or the network at compose, a per-request allocation storm. Find that, rather
  than raising the ceiling.
- **`[unobserved]`** — the run produced a number but never confirmed it measured anything. The
  sample never reached its ready line, or the request burst did not return success. The app is
  broken, not slow; the number is meaningless and the gate refused to read it.
- **`[unmeasured]`** — the budget places a ceiling on a metric the run carries no sample for. The
  measuring half stopped measuring something. This is a breach rather than a skipped line
  precisely because an unmeasured budget line otherwise reads exactly like a passing one.
- **`[undersampled]`** — the statistic came from fewer observations than the budget requires. A
  "min" over one run is just that run.
- **`[zero-cost]`** — an assembly the budget requires to be absent is in the minimal app's
  output. Something now references a companion the minimal shape should not carry.

## The decisions, and what they cost

**The gate reads `min`, not mean or median.** Wallclock on a shared runner is a floor plus
noise, and the noise is one-sided — a neighbouring job can only ever make a boot slower. The
minimum over N runs is therefore the least contaminated estimate of the thing being defended,
and the only statistic whose spread does not widen as the runner gets busier. This was not a
hunch: the same six boots on the same machine measured min 420 ms quiet and min 1378 ms under
load (a 3.3× shift), while the *median* hot path shifted 10× (0.52 ms → 5.56 ms) against a 1.9×
shift in the minimum. The median and maximum are recorded in the measurement file for a human;
neither is gated on.

**The ceilings are deliberately generous, and bounded.** 6000 ms against a 420 ms baseline is
~14× slack, and it is not Phase 16's 2000 ms. 2 s was measured on a deployment target and is a
fine claim about a deployment; as a CI ceiling on a shared runner whose loaded minimum was
1378 ms it would flake, and a gate that flakes is a gate people disable. What keeps "generous"
from becoming "vacuous" is not anyone's memory: every green run prints the measured headroom
ratio, and `ToolUp.Platform.Build.Tests` **refuses a ceiling more than 20× its recorded
baseline**. Widening past that fails the ordinary suite.

**It measures `samples/MinimalApp`, not `samples/HelloWorld`.** The phase named
`samples/HelloWorld/HelloWorld.Server/Program.fs`; that file does not exist (the sample's
composition root is `Server.fs`), and HelloWorld is the *reference* app rather than the minimal
one — it composes a module and two console-printing sinks, one of which emits a line per
request and would sit inside the hot-path measurement. HelloWorld's own header points at
MinimalApp as the minimal comparison. MinimalApp is what "the minimal shape, all `No*` defaults
active" means.

**The hot path is `POST /api/PlatformInfoApi/GetPlatformInfo`, not `/health`.**
`MetricsMiddleware`, `RequestTimingMiddleware` and `RateLimiting` all short-circuit on paths
starting `/health` so probes do not pollute metrics — so a `/health` probe skips exactly the
middleware this budget exists to measure. `PlatformInfoApi.GetPlatformInfo` is `AllowAnonymous`,
auto-injected into every composition, and is the call the client shell makes before sign-in, so
it exists on the minimal shape and traverses the full pipeline.

**Composition is not timed in-process.** The obvious cheaper design — call `compose` in a test
and time it — was rejected. `ServerApp.composeOnly` does not exist (the Azure Functions and AWS
Lambda host adapters both call it "hypothetical"), and the low-level `compose` it would wrap is
`EditorBrowsable(Never)` with a 33-argument positional signature its own doc comment says
"changes whenever new SDK features land". A gate bound to that signature would need editing
every time the SDK grew, which is the opposite of a guard you can leave running for a year.

## What is deliberately NOT here

- **No `ToolUp.Platform.PerfBudget.Tests` project.** The phase named one. A new Expecto pack
  joins `VerifyAll`, which is one process running twelve packs on a shared runner — the worst
  possible place for a wallclock assertion, and it would grow the critical path. The deciding
  half's tests ride the existing `Build` pack instead, and the measuring half is its own CI job.
- **No `SDK-ADOPTION.md` row.** That matrix is generated from consumers' own `sdk-adoption.json`
  manifests and from forge phases marked `consumer_facing`. This phase ships no consumer-visible
  surface, so there is nothing for a consumer to adopt and no row to generate.
- **No synthetic "heavy module" fixture.** The phase asked for one to prove the gate has teeth.
  A fixture that makes a *real app* slow on purpose is runtime surface in the repo, which this
  phase promised not to add, and it would only ever prove that a slow app is slow. `-TeethCheck`
  proves the stronger thing on **this run's real measurements**: re-decided against a 0.001 ms
  budget, the gate must go red, and the CI job fails if it does not. The regression case is
  additionally pinned by a committed measurement fixture in the test pack.

## See also

- `perf-budgets.json` — the budget, with each ceiling's provenance in its `notes` block
- `dev-scripts/perf-budget-gate.ps1` — the measuring half
- `src/ToolUp.Platform.Build/Build/SDK.PerfBudget.fs` — the deciding half
- `src/ToolUp.Platform.Build.Tests/PerfBudgetTests.fs` — its laws
- `docs/migrations/213-lighthouse-core-web-vitals-budget-gate.md` — the same split, for the
  browser-side budget
