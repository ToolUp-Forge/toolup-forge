# Budgets — one shape for every resource ceiling

Resource-exhaustion defence in this SDK is a *budget*: a subject may spend so much of a resource in
a window, and something legible happens when it cannot. Several such mechanisms exist — compute
submissions, render cost, and AI token and monetary spend — and before Phase 689 each had
invented its own vocabulary. This page is the shape they share, where each budget actually lives,
and how a refusal reaches an operator.

If you are wiring compute budgets specifically, read this page for the vocabulary and
[`external-compute.md`](external-compute.md) for the seam they decorate.

---

## The four parts

Every budget in the SDK is these four things, and the seam gives each one a name.

| Part | Type | What it answers |
|---|---|---|
| **declare** | `BudgetSubject`, `BudgetPeriod`, `BudgetClaim` | who is budgeted, over what window, against which ceilings |
| **check** | `BudgetPolicy` → `BudgetVerdict` | may this request proceed — and is it close to the edge |
| **account** | `BudgetAccount` | what a refusal or a threshold crossing records |
| **store** | `IBudgetLedger` | where consumption lives, and how a check and its reservation stay indivisible |

`BudgetPeriod` / `BudgetSubject` / `BudgetClaim` / `BudgetDenial` / `BudgetWarning` /
`BudgetVerdict` / `BudgetUsage` / `BudgetAccount` / `BudgetPolicy` live in `ToolUp.Platform.Core`
(`Shared/Budget.fs`) and are Fable-safe, so a client renders a refusal without pulling the server
tier in. `IBudgetLedger`, its blob layout and the two shipped ledgers are `ToolUp.Platform.Server`.

### One ceiling is a claim, and one predicate decides them all

A `BudgetClaim` is a ceiling plus both halves of the measurement against it:

```fsharp skip=fragment
{ Dimension = "tokens-per-hour"; Ceiling = 100_000M; Spent = 99_000M; Requested = 2_000M }
```

and the whole check is `Spent + Requested > Ceiling`. That one predicate covers ceilings that look
unrelated:

| Budget | Dimension | Ceiling | Spent | Requested |
|---|---|---|---|---|
| compute concurrency | `concurrency` | max in flight | runs in flight | `1` |
| compute run duration | `run-duration` | cap, seconds | `0` | declared, seconds |
| compute allowance | `period-allowance` | units per period | consumed | this run's cost |
| AI tokens | `tokens-per-hour` | cap | tokens used | estimate |
| AI spend | `spend` | ceiling | spent | estimated cost |

Two rules follow from the shape rather than from a convention:

- **A ceiling of `<= 0` is unrestricted**, on every dimension. The *absent* budget and the *empty*
  budget are therefore the same value: an unconfigured scope is unrestricted by construction rather
  than by a missing branch (GP 11), and a deployment that composes nothing pays nothing (GP 13).
- **A request landing exactly ON the ceiling is admitted; the next is refused.** An allowance of 100
  units admits the run that takes consumption to 100, which is what "an allowance of 100" means.

Quantities are `decimal` and abstract — cost units, tokens, runs, seconds. Never a currency: a
monetary budget denominates its own units and says so in its dimension label, and the SDK core
carries no pricing vocabulary or rounding policy (GP 1).

### The period is a storage key, not a counter to reset

`BudgetPeriod` is `Perpetual | Hourly | Daily | Monthly`, and `BudgetPeriod.key` renders the window
`now` falls in — `"perpetual"`, `"2026-08-05T14"`, `"2026-08-05"`, `"2026-08"`. That key is part of
the ledger's identity, so **a period reset is free and cannot fail to run**: the next period is a
different key that does not exist yet, and a key that does not exist reads as zero consumption.
There is no reset job, so there is no reset job to be missed — the failure mode where a control
silently becomes an outage.

Keys are always computed in **UTC**. A boundary that moved with the server's timezone would put two
replicas of one deployment in different periods for an hour twice a year.

### Refusals and warnings are data

A refused request produces a `BudgetDenial` naming **which** ceiling, for **which** class, in
**which** period, with `Spent` and `Requested` kept apart so a caller can tell "you are already
over" from "this one request would take you over" — different remedies, and often the recipient is
an agent deciding whether to narrow its search or wait. When several ceilings are breached at once
the **first one listed** is reported, deliberately: a refusal naming three problems invites fixing
the wrong one.

An *admitted* request that pushes consumption past the warning threshold
(`BudgetPolicy.defaultWarnThreshold`, `0.8`) produces a `BudgetWarning` instead — a leading
indicator, reported **once per subject per period** so it does not decay into a line an operator
filters out. `BudgetVerdict` carries all three answers:

```fsharp skip=fragment
match BudgetPolicy.verdict subject periodKey BudgetPolicy.defaultWarnThreshold claims with
| BudgetVerdict.Allowed -> // proceed
| BudgetVerdict.NearLimit warning -> // proceed, and record
| BudgetVerdict.Refused denial -> // refuse, and record
```

`BudgetAccount` is what "record" means — a record of two functions rather than an interface, because
accounting is deployment policy (one domain writes a typed audit event, another emits a metric) and
because expressing it as data means composing one is a `let`. `BudgetAccount.silent` is the explicit
opt-out; an allowed verdict on an unthreatened budget records nothing at all.

### The ledger makes a check and its reservation one operation

`IBudgetLedger.Reserve` takes the pure decision **in** rather than exposing the row and trusting the
caller:

```fsharp skip=fragment
abstract Reserve:
    key: BudgetLedgerKey * cost: decimal * decide: (BudgetUsage -> Result<unit, BudgetDenial>) ->
        Async<Result<BudgetUsage, BudgetDenial>>
```

The concurrency this closes is the one a budget exists to bound: N requests arriving at once.
Read-then-decide-then-write admits all N, because every one of them reads the same pre-burst row and
concludes there is room — which is not a rare race but the *expected* behaviour of an agent fanning
out.

Two ledgers ship. `BlobBudgetLedger` serialises writers per key with a semaphore and, when the
composed `IBlobStorage` also implements `IConditionalBlobStorage`, makes the write an **ETag
compare-and-swap** that extends the guarantee across replicas. On a backend with no ETag support you
get the in-process tier only, so with N replicas a concurrency ceiling binds per replica and the
effective ceiling is up to N× the configured one — a real weakening, deliberately not resolved by
taking a distributed lock (that would put every request behind a round-trip to tighten a bound whose
job is to be approximately right about an already-approximate resource). The remedy is a conditional
blob backend, which every cloud one is. `InMemoryBudgetLedger` is genuinely atomic and genuinely
non-durable — for tests and single-node development.

**A ledger fails open.** A storage error reads as no consumption and admits. The failure direction a
budget may have is admitting work it should have refused; the direction it may **not** have is
turning a transient storage blip into a deployment-wide refusal, because a budget that fails closed
is a budget an operator switches off after the first incident — which leaves them with no budget at
all.

The ledger holds the **counter**, never the ceilings. Where a domain's budget is *configured* is the
domain's own question — compute keeps it in a blob beside the usage row; a token budget will read it
from per-team config — and a seam that owned both would force every domain to move its policy
storage to satisfy an interface.

---

## Where each budget lives

| Budget | Ceilings | Enforced at | On the seam? |
|---|---|---|---|
| **Compute** (`ComputeBudget`) | concurrency, run duration, period allowance | `BudgetedComputeDispatcher` over `IExternalComputeDispatcher`, **and** the fit-job enqueue path | yes — its decision is `BudgetPolicy.check`, its counter is `IBudgetLedger` |
| **Hosted-tree render cost** (`HostRenderBudget`) | max nodes, max depth, render time | in-band on the client render path, plus a CI fixture gate | no, by decision — see below |
| **Peer cascade** (`PeerCascadePolicy`) | hops remaining, route length, identifier length | receiver-side, on every inbound peer call | no, by decision — see below |
| **AI tokens** (`AITokenBudgetPolicy`) | per-user, per UTC hour | `BudgetEnforcingProvider`, in the AI provider decorator chain | yes — `BudgetPolicy.verdict` over a `BudgetClaim`, consumption summed from telemetry rather than reserved |
| **AI monetary spend** (`AISpendBudgetPolicy`) | per-user and per-scope, each over its own `BudgetPeriod` | `SpendEnforcingProvider`, outermost in the same chain | yes — same predicate, same denial shape; the currency lives in the rate card, never in the seam |

### Compute budgets

Off by default: `ServerConfig.ComputeBudget = FeatureMode.Disabled` registers nothing, wraps nothing, and
consults nothing, so an existing deployment is byte-for-byte what it was (GP 11 + GP 13).
`FeatureMode.Enabled` registers the blob-backed store, builds the shared guard, and wraps the
composed dispatcher.

Per-scope ceilings are stored under `_platform` at `compute-budget/<scope>/budget.json`, with
consumption at `compute-budget/<scope>/usage/<period>.json` — the ledger's own layout, with
`"compute-budget"` as the domain segment. A submission declares a `SubmitterClass`
(`Human` / `Scheduled` / `AgentInitiated`) and a class with its own entry is governed **entirely**
by that entry, never by a merge with the default: an operator writing "agents get 10 units" is not
also silently granting them the default concurrency.

Compose the memo **outside** the budget —
`MemoizedComputeDispatcher(BudgetedComputeDispatcher(backend))` — so a cache hit spends no allowance
and holds no concurrency slot.

### The two budgets that stay in band, and why

**Render-cost budgets are not moved onto the seam, and that is a decision rather than an omission.**
`HostRenderBudget` measures one tree in one render, on the client, against limits the consumer
declared in code. It has no scope, no period, no accumulation and no store — three of the seam's four
parts are empty, and the fourth (`BudgetClaim`'s predicate) it would satisfy only by inventing a
subject and a period key for a measurement that has neither. Routing it through a ledger would add a
Server-tier dependency to a Client-tier measurement that is deliberately synchronous and free, and
would make a per-render check pay for state it does not have. The seam's vocabulary is the honest
overlap and the two are cited in each other's docs; the machinery is not.

The **receiver-side cascade budget** is the same finding from the other direction. Its ceilings are
per-call shape bounds derived from the validated principal — hops remaining, route length,
identifier length — and its whole value is that they are re-derived server-side on *every* inbound
call rather than accumulated anywhere. There is nothing for a ledger to hold, and a period key would
be meaningless for a bound that resets per request.

Both remain useful as **conformance instances**: a proposed change to the seam that either of them
could not express in principle is a change worth re-reading, because the seam's job is to be the
shape every ceiling in the SDK has.

### Performance budgets are a CI gate, not a runtime ceiling

`perf-budgets.json` at the repo root is the other thing in this repository called a budget, and it is
deliberately not on the seam: it bounds what the SDK *costs*, not what a subject may *spend*, and it
is decided in CI rather than in any deployment. It has three blocks of the same shape — ceiling,
baseline, minimum samples, the `min` statistic, and a note per ceiling:

| Block | Measured by | Metrics |
|---|---|---|
| top level (server, Phase 192) | `dev-scripts/perf-budget-gate.ps1` booting `samples/MinimalApp` | `coldStartMs`, `hotPathMs`, plus the absent-assembly rule |
| `client` (Phase 849, 909) | the same script running `ClientBench` in `src/ToolUp.AI.Client.Tests` under Node, after two production `vite build`s of `samples/MinimalClient` | `bootMs`, `decodePerResponseUs`, `viewPerDispatchUs`; `minimalBundleKiB`, `shellBundleKiB`, `shellFirstLoadKiB` |
| `load` (Phase 886) | the same script running `load gate` in `src/ToolUp.RAG.Benchmarks` | retrieval p95 and blob reads per query; fact point read, population read, assert and batch assert p95, and blob reads per point read |

All three are decided by `ToolUp.Platform.Build`'s `PerfBudgetGate` (`VerifyPerfBudget` /
`VerifyClientPerfBudget` / `VerifyLoadPerfBudget`), print every ceiling's headroom ratio on a green
run, and are held by `ToolUp.Platform.Build.Tests` to at most 20x their recorded baselines. The client
numbers — and which of them transfer from Node to a browser — are recorded in
[`../migrations/849-browser-runtime-benchmark.md`](../migrations/849-browser-runtime-benchmark.md);
the server ones in [`../migrations/192-cold-start-perf-budget-ci-gate.md`](../migrations/192-cold-start-perf-budget-ci-gate.md).

**The two bundle sizes (Phase 909) are the one place the headroom rule differs.** `minimalBundleKiB`
is the JavaScript a production build of the minimal client sample ships; `shellBundleKiB` is the same
for the SDK shell's module graph (`SDK.Client` as the entry, every export kept, built by
`samples/MinimalClient/vite.shell.config.mts`) — the one that holds the remoting proxies and the
generated client module, so the one that grows when a generated module does. The clocks carry about
14x slack because wall-clock noise on a shared runner is one-sided; a size has no noise at all (three
builds from two separate Fable compiles wrote the same bytes), so the minimal bundle and the shell's first
load are held tight: the build test refuses more than 1.1x. The whole shell carries 1 MiB above its baseline,
and the test refuses more. Since Phase 1168 a user's wait follows the first load, so the whole-bundle figure is
watched rather than held close, and the first-load ceiling is the one that stops growth. The gate script
builds both into fresh directories under `artifacts/perf-budget/bundles/`, so a failed build is an
unobserved bundle rather than the last run's size, and its `-TeethCheck` decides the sizes against a
bundle-only 1 KiB block so they are shown red on their own.

**The shell's first load (Phase 1168).** `shellBundleKiB` sums every file, so it cannot see a split. `shellFirstLoadKiB`
sums what a browser fetches before anything runs: the entry chunk and its static imports, walked through the
`.vite/manifest.json` the shell build writes. AG Grid and AG Charts sit behind dynamic imports, in
`AgGridRuntime` and `AgChartRuntime`. The gate script refuses a build where either stopped being on demand, and
its `-TeethCheck` feeds that refusal a collapsed manifest. The binding files import `ag-grid-react` and
`ag-charts-react` only through `[<Import>]`, because neither package declares `sideEffects`: one unused import
at the top of a file keeps the whole library in the entry.

**The Fuaran-UI renderer and node vocabulary (Phases 1202, 1186).** A forge surface authored as a Fuaran-UI tree
reaches the shell through an internal seam in three files. `TreeSurface` mounts a component that arrives on demand: a
keyed `React.lazy` behind `Suspense`, which imports no Fuaran package, so a file the shell imports at boot may call it.
`TreeLowering` holds the authoring helpers (`row`, `column`, `button`, …), the islands and `lower`. `TreeLoweringRuntime`
holds everything that imports the renderer. While a deferred component loads, the surface shows the grid's placeholder
(`role="status"`, `aria-busy`); a load that fails rejects into `ModuleBoundary`, whose **Reload module** loads it again.

A surface can take either of two shapes, and only one is free at boot:

- **Deferred, the shape for a surface the shell imports at boot.** The surface's static module (its definition and
  `update`, which the shell reads at boot) calls `TreeSurface.deferred` with a dynamic import of a component in a file
  of its own. That file builds the tree with the helpers and draws it with
  `TreeLoweringRuntime.Tree (TreeLowering.treeProps host dispatch node)`. The vocabulary, its compute closure and the
  renderer then arrive together when the surface first mounts. A file reached this way from a package a consumer
  restores from a feed needs a `.js` re-export beside it, as `TreeLoweringRuntime.js` is.
- **Lowered.** A view that builds its tree where it is and calls `TreeLowering.lower` keeps only the renderer on
  demand. The helpers reach `Fuaran.UI`'s node vocabulary, whose closure holds `Fuaran.Compute.DataFrame` and
  `Fuaran.Core.Column`, so the vocabulary is in the importing file's static closure. That is fine for a view nothing
  imports at boot, and it is what the gate refuses for one the shell does.

The shell build names any renderer module its first load holds into a chunk called `FuaranUIRendererAtBoot`, and any
`Fuaran.UI` vocabulary module into `FuaranUIVocabularyAtBoot`. The gate script refuses a build that has either, one
whose `TreeLoweringRuntime` chunk is in the first load, or one in which that chunk is built and no dynamic import of the
first load reaches it, directly or through a deferred surface's chunk. Its `-TeethCheck` feeds it a static renderer and
a static vocabulary, which must be refused by name, and the deferred shape, which must pass. A shell with no tree
surface builds none of these chunks and pays nothing.

| On-demand chunk | Bytes | Gzipped | Fetched when |
|---|---:|---:|---|
| `AgGridRuntime` | 1,140,619 | 316,514 | the first grid mounts |
| `AgChartRuntime` | 1,178,697 | 343,679 | the first chart mounts |
| `TreeLoweringRuntime`, lowered surfaces (the renderer: `Fuaran.UI.Renderer`, `.Renderer.Core`, `.Charts`) | 284,224 | 85,698 | the first tree surface mounts |
| `TreeLoweringRuntime`, deferred surfaces (the renderer, the vocabulary and its compute closure) | 616,023 | 171,522 | the first tree surface mounts |
| a deferred surface's own chunk (the team manager's tree) | 11,605 | 4,258 | that surface mounts |

Measured with three real surfaces authored as trees (a team manager, a module-visibility admin, the command palette
behind an island): Phase 1160's surfaces restored over Phase 1202's tree (`0fc90bc4`, with `8d15b00e` reverted) in a
scratch tree, the same three surfaces in each arm, a production `vite build` of the shell configuration above,
2026-11-20. The scratch tree pins `Fuaran.UI` 0.98.1, as Phase 1202's measurement did:

| Shell | First load, bytes | First load, KiB | × no tree surface | Whole shell, KiB |
|---|---:|---:|---:|---:|
| no tree surface | 1,511,762 | 1,476.33 | 1.00 | 3,741.29 |
| three surfaces, renderer imported statically (refused: `FuaranUIRendererAtBoot`) | 2,128,132 | 2,078.25 | 1.41 | 4,343.34 |
| three surfaces lowered, renderer on demand (refused: `FuaranUIVocabularyAtBoot`) | 1,846,729 | 1,803.45 | 1.22 | 4,346.15 |
| three surfaces deferred | 1,507,893 | 1,472.55 | 1.00 | 4,353.46 |

The first three rows reproduce Phase 1202's figures to the byte, except the lowered row, which names the vocabulary
into its own chunk and so splits 486 bytes differently. Deferring the trees takes the vocabulary's 379,258 bytes and
the renderer out of the first load, which returns to its size with no tree surface; the falsifier is
`Fuaran.Compute.DataFrame`, whose name occurs ten times in the first load of the lowered build and in no first-load
file of the deferred one. The first surface to mount then pays the whole closure once. Phase 1202 named two routes to
this figure: this one, and cutting the vocabulary's compute closure on the Fuaran-UI side (fuaran#2539), which had not
landed when this was measured. Deferring closed the gap on its own; the Fuaran-UI cut would shrink what the first tree
surface fetches, and on its own would not bring a lowered surface's first load back under its ceiling.

**What a first load and a first tree surface cost in a browser.** Real Edge 157 over the DevTools protocol, the four
builds above served side by side, one page per build that imports the shell and mounts nothing (`import()` to
resolved, as Phase 1168 measured), then imports the chunks the first tree surface fetches (`tree`). 15 loads per cell
with the arms alternated per load, median milliseconds. Cold loads transferred exactly each arm's gzipped first load and
warm loads nothing. Measured 2026-11-20 on 16 logical cores:

| Profile | Cache | No tree surface | Renderer eager | Lowered | Deferred | First tree surface, lowered | First tree surface, deferred |
|---|---|---:|---:|---:|---:|---:|---:|
| desktop, no throttle | cold | 76 | 78 | 76 | 52 | 10 | 20 |
| desktop, no throttle | warm | 75 | 73 | 75 | 44 | 8 | 18 |
| mid laptop: 4x CPU, 10 Mbps, 40 ms | cold | 625 | 789 | 722 | 565 | 148 | 257 |
| mid laptop: 4x CPU, 10 Mbps, 40 ms | warm | 264 | 308 | 284 | 187 | 19 | 42 |
| low phone: 6x CPU, 1.6 Mbps, 150 ms | cold | 2,940 | 3,612 | 3,163 | 2,499 | 640 | 1,158 |
| low phone: 6x CPU, 1.6 Mbps, 150 ms | warm | 751 | 784 | 860 | 490 | 47 | 108 |

The deferred first load is faster than the one with no tree surface, and mostly not because of the deferral: the
bundler splits that first load into nine files, where the others have three to five, and the browser compiles
separate files in parallel (the time after the last byte arrives, on the desktop, is 37 ms against 67). Read the
first-load bytes for the like-for-like comparison and the last two columns for what deferring moves: the first tree
surface pays the vocabulary as well as the renderer, about twice what a lowered surface pays, once. The warm cells on
the low phone are the noisiest (the minimum of 15 is 410, 614, 537 and 245 ms across the four builds). The study's
figures for the
same profiles (forge#1160's spike, renderer eager, against main, 2026-10-31) were: desktop cold 85 and 117 ms, mid
laptop cold 654 and 899 ms, low phone cold 2,495 and 3,987 ms. The renderer's chunk alone, imported after the first
load, cost 24, 279 and 1,327 ms cold.

### A tree surface is judged on a browser dispatch, not on `viewPerDispatchUs`

`viewPerDispatchUs` times the construction of a 200-row Feliz view under Node: the render hook builds the element tree
and the time is read. It cannot see React's commit, the browser's layout, or work a component defers into commit, and a
tree surface does most of its work in components the renderer returns. It stays the regression guard for the shell's
Feliz views, which is what its ceiling was set against, and it is no guide to a tree.

The figure a tree surface is judged on is one host dispatch in a browser: the view rebuilt from a new model, React's
render and commit, and a forced layout. `ClientBench --browser-dispatch` takes it, and the perf-budget gate script
passes the flag. `client-bench-browser.mjs` bundles `ClientBenchTables` with the shell sample's Vite: the 200-row table
as Feliz, and as a tree of the helpers' rows, text and buttons drawn the way a deferred surface draws it. It serves the
bundle cross-origin isolated, so the clock resolves to 5 µs, under the renderer's reference stylesheet and the shell's
bridge, and drives headless Edge over the DevTools protocol with `gc` exposed (Chrome when there is no Edge;
`TOOLUP_BENCH_BROWSER` names another Chromium-family browser). Per profile (no throttling, and 6x CPU, the study's low
phone) it mounts each table, runs 15 dispatches to warm, then times 30, each after `gc()`. The Feliz table is measured
again last, as the control for drift across the run. A reading in which a mount built under 100 elements, a dispatch
changed no DOM, the page was not isolated or `gc` was missing is refused. The result is the `browserDispatch` lever in
the measurement file, recorded and never decided; a machine with no browser records the absence.

Measured 2026-11-20 on this tree (`Fuaran.UI` 0.104.0, React 19.2, Edge 157, 16 logical cores), two runs of 30
samples each, median milliseconds per dispatch (render and commit + forced layout):

| Profile | Feliz table | Tree table | Feliz table again (control) | The study, Feliz / tree (renderer 0.98.1) |
|---|---:|---:|---:|---:|
| desktop | 1.56 / 1.52 (1.25 + 0.35) | 14.76 / 13.11 (14.54 + 0.23) | 1.86 / 1.64 | 1.2 / 16.8–20.5 |
| low phone: 6x CPU | 22.8 / 20.0 (16.9 + 5.4) | 208.2 / 190.8 (205.7 + 2.7) | 22.2 / 20.1 | 19 / 355 |

The tree table mounts 2,004 elements and the Feliz table 1,004; each dispatch changed two DOM records on both (the
tick and the bumped value), where the study counted 400 `innerHTML` reparses per dispatch on the 0.98.1 renderer, so
that defect is gone from the renderer this tree pins. The tree's dispatch is about nine times the Feliz table's on both
profiles, almost all of it render and commit, not layout.

The frame is the ceiling that matters for a tree: 16.7 ms on a desktop, and on the low phone the equivalent of about
100 ms. A rebuilt 200-row tree fits the first and is about twice the second; the study's subtree memoisation (its
lever 1) is what would take a rebuilt table under both.

**The kept table.** The leg's third arm, `tree-kept`, is the same 200 rows authored as [the authoring
guide](authoring-trees.md) says: each row built through a `Keep` keyed on its row record, its ids from the row's
identity, drawn through the lowering, which keeps its render context and turns the subtree memo on. A dispatch replaces
one row record and the tick, so the renderer builds the heading and that row and serves the other 199 rows' elements from
the memo. Measured 2026-11-21 on the same machine (Edge 157), one run of 30 samples, median milliseconds per dispatch:

| Profile | Feliz table | Tree, rebuilt | Tree, rows kept | Feliz again (control) |
|---|---:|---:|---:|---:|
| desktop | 2.14 | 15.65 | 1.66 | 2.07 |
| low phone: 6x CPU | 25.79 | 106.22 | 15.47 | 12.20 |

Both tree arms mount 2,004 elements and change two DOM records per dispatch, so the kept arm does the same work in the
document; what it stops doing is building and reconciling the 199 rows that did not change. A kept tree is inside the
frame on both profiles, at about the Feliz table's cost. (The low-phone Feliz arm's first reading is noisier than its
control; the kept arm's p90 is 20.2 ms against the rebuilt tree's 191.9.)

### A moved module view is measured in the browser leg, before and after

Each real module view that moves from Feliz to a tree is an arm pair of the same browser leg, so its cost is measured
in a browser rather than inferred from the synthetic table. `ClientBenchTables.mount` mounts a view under two names:
`<view>-feliz`, the Feliz page it replaced, kept in `FelizReferenceViews.fs`; and `<view>-tree`, the shipped tree,
mounted by the `Surface` component its `TreeSurface.deferred` loads (the leg mounts it directly, since a timed
synchronous render cannot wait for the dynamic import). Both arms start from a model reached through the module's own
`init` and `update`, and each timed dispatch goes through that `update` too, with the effects it returns dropped: the
leg times the view, not the remote call. `client-bench-browser.mjs` runs each view's Feliz arm, its tree arm and its
Feliz arm again (`<view>-feliz-control`) as that view's drift control, on the same page, clock and refusals as the
tables above.

A view that shows a list is measured on two dispatches, because a kept row is served by the identity of its record
and the two differ in exactly that. **One renamed** replaces one record and leaves the other 199 the objects they
were, which is what a dispatch inside the page does. **Loaded again** (`<view>-reload`) replaces every record with a
new object, one of them with a new name, which is what a server response does.

Measured 2026-11-26 on this tree (`Fuaran.UI` 0.107.0, chromium headless shell 131 through `TOOLUP_BENCH_BROWSER`,
16 logical cores), three runs of `pwsh ./dev-scripts/perf-budget-gate.ps1 -SkipServer -SkipLoad` with CPU load 79, 9
and 9 percent at their starts. Desktop, median milliseconds per dispatch, minimum over the runs with the range in
brackets:

| View, and its dispatch | Feliz page | Shipped tree | Feliz again (control) | Elements, Feliz / tree | DOM records per dispatch |
|---|---:|---:|---:|---:|---:|
| The team manager, 200 teams, one renamed | 3.30 (0.13) | 1.79 (0.11) | 3.13 (0.37) | 1,605 / 2,807 | 1 |
| The team manager, 200 teams, loaded again | 3.38 (0.16) | 2.04 (0.10) | 3.39 (0.08) | 1,605 / 2,807 | 1 |
| The module-visibility editor, 40 modules, the rule kind flipped | 0.73 (0.02) | 1.07 (0.04) | 0.65 (0.09) | 258 / 346 | 125 |
| Platform Management's teams tab, 200 teams, one renamed | 8.30 (0.16) | 3.15 (0.22) | 8.15 (0.17) | 3,837 / 8,045 | 7 |
| Platform Management's teams tab, 200 teams, loaded again | 8.34 (0.26) | 34.04 (0.28) | 8.51 (0.70) | 3,837 / 8,045 | 7 |

Every view's control stayed within 15 percent of its first reading in every run. With its rows kept, a tree dispatch
costs about half the Feliz page's on the two team lists: the Feliz page builds and reconciles all 200 rows, the tree
the one that changed. A tree whose rows are all rebuilt costs about four times the Feliz page, and that is what a
reload is unless `update` keeps the records that did not change. The team manager's does (`Memo.keepEqual` in its
`TeamsLoaded`): before it did, on the same machine and three runs at 15, 10 and 3 percent load, its tree's reload was
14.64 ms (1.93) against the Feliz page's 3.27. Platform Management's does not yet: each of its reloads first sets the
list to `Remote.Loading`, which drops the records it held, and its view draws a reload in flight as the loading line,
so keeping them needs the view to go on showing the list meanwhile. The first mount, one cold sample per run, is 26
ms against 12 for the team manager and 63 against 28 for Platform Management: kept rows pay their first render in
full.

The 6x CPU profile, the same three runs, median milliseconds, the minimum over the runs:

| View, and its dispatch | Feliz page | Shipped tree | Feliz again (control) |
|---|---:|---:|---:|
| The team manager, one renamed | 32.49 | 17.99 | 31.62 |
| The team manager, loaded again | 31.24 | 19.77 | 34.29 |
| The module-visibility editor | 7.33 | 11.03 | 6.98 |
| Platform Management's teams tab, one renamed | 92.18 | 33.67 | 91.02 |
| Platform Management's teams tab, loaded again | 81.92 | 275.52 | 89.88 |

Read the 6x table more loosely than the desktop one: two controls moved more than 15 percent from their first reading
(18 and 19 percent, both on a reload view), and on a loaded machine the DevTools CPU throttle is intermittent, so a 6x
arm's samples include unthrottled ones. Four earlier runs of this leg, at 99, 29, 83 and 12 percent load with other
builds running, gave no 6x reading at all (one run's 6x median for Platform Management's Feliz page was 19.99 ms
against 12.55 on the desktop). Take 6x figures on a quiet machine, and check each control before quoting an arm. The
record is `uiTreeViewsBrowserDispatch` in [`perf-budgets.json`](../../perf-budgets.json); recorded, never gated.

**How a batch adds its views' arms.** A phase that moves more views to trees copies this shape:

1. Keep each view's Feliz page in `FelizReferenceViews.fs`, as the migration kit's parity assertion already asks.
2. In `ClientBenchTables.fs`, write the view's seed and dispatch beside `teamManager ()`: a model folded from the
   module's `init` through its `update`, at the size the view's phase names, and a `ModuleStep` whose `Next tick` is
   the message one dispatch sends, chosen so that every dispatch changes what the page shows. For a list, write both
   dispatches: one record replaced with the rest kept by reference (`oneRenamed`), and every record new (the
   `-reload` arm). A dispatch that replaces every record measures a reload, whatever it is called.
3. Add `<view>-feliz` and `<view>-tree` to `tableKinds` and to `mount`: the first draws the Feliz page, the second the
   tree module's `Surface` with the props its deferred surface passes.
4. Add the view, and its `-reload` twin where it shows a list, to `VIEWS` in `client-bench-browser.mjs`, with the
   label the summary line prints. The leg then runs the
   pair and the control, and refuses a mount under 100 elements or a dispatch that changes no DOM, so seed the view
   past that.
5. Take three runs of the gate script on a quiet machine, note each run's load at its start, and record the minimum and
   range per arm as a note in `perf-budgets.json` and a row in a table like the one above. Move no ceiling and add no
   gate.

### The Fuaran-UI stylesheet

A tree is styled by the renderer's `fuaran-reference.css` and the shell's `fuaran-bridge.css`, which the client CSS
entry imports ([theming](theming.md)). CSS is not in `shellFirstLoadKiB`, which sums JavaScript, so its cost is
recorded here. Measured 2026-11-15 with `Fuaran.UI.Renderer` 0.104.0, a production `vite build` of
`samples/MinimalClient` with and without the two imports:

| CSS asset | Bytes | Gzipped |
|---|---:|---:|
| the sample's own rules | 91 | 118 |
| with the reference stylesheet and the bridge | 68,802 | 10,163 |
| added to every first load | 68,711 | 10,045 |

The source file is 152,796 bytes; minifying takes it to under half. The 68,711 minified bytes are 4.5 % of the
shell's JavaScript first load above (1,511,762 bytes, also minified).

**It stays on the entry rather than riding the renderer's chunk.** Vite would split a stylesheet imported from
`TreeLoweringRuntime` into that chunk and fetch it with the renderer, saving those bytes on a page with no tree. But
the import would then sit in the SDK's packaged Fable source, so every consumer's bundler would have to resolve the
`@nuget/` alias, and a consumer with its own stylesheet pipeline could no longer choose where the rules go; the
Node test harness that loads the shell's modules would need a CSS stub. At 10 KiB the saving does not pay for that.
Revisit it if a first-load ceiling comes to count CSS, or the stylesheet grows several-fold.

**A template app also carries the canonical Tailwind block.** The template clients embed it above the two imports
(Phase 1363), so an app made from a template pays for the shell's utilities and tokens as well. Measured 2026-11-16
with `Fuaran.UI.Renderer` 0.104.0 and the pinned `tailwindcss` 4.3.0: a production `vite build` of the
`platformsdk-application` client's CSS entry, after a Fable compile of the client so Tailwind scans the shell's
classes, without and with the block (gzip as Vite reports it):

| CSS asset | Bytes | Gzipped |
|---|---:|---:|
| the reference stylesheet, the bridge and the template's own rules | 68,817 | 10.18 kB |
| with the canonical block | 118,807 | 19.50 kB |
| added to every first load by the block | 49,990 | 9.32 kB |

The 49,990 bytes are 3.3 % of the shell's JavaScript first load above. They are the price of the shell drawing at
all: without the block none of the shell's utility classes (`bg-sidebar`, `bg-brand`, `border-border`, …) is
generated, and the shell renders unstyled.

**The whole first load of a template app (Phase 1365).** Once the template clients declare the SDK bundle's npm
packages and plugins, a freshly instantiated template bundles with no edit. Measured 2026-11-16 on an unmodified
`safer` app (the gate-version SDK packages from this tree, `vite` 8.3.8), production `vite build`, the files
`index.html` loads before the shell draws:

| Asset | Bytes | Gzipped |
|---|---:|---:|
| entry JavaScript | 1,409,451 | 354.58 kB |
| preloaded `react` chunk | 8,195 | 3.16 kB |
| CSS entry (the canonical block, the reference stylesheet, the bridge) | 118,807 | 19.50 kB |
| first load | 1,536,453 | 377.24 kB |

The grid and chart runtimes stay on demand (1,139,797 and 1,178,166 bytes, fetched only by a surface that draws
one), as in the shell's own bundle above. The other three templates' entries measured within 6 KB of this one
(1,403,750 to 1,407,800 bytes) with the same CSS. That split depends on the bundler: the same clients locked to
`vite` 8.0.13 (`rolldown` 1.0.1) inlined both runtimes into a 3,696,270-byte entry, so the template lock files were
regenerated from a fresh resolution rather than patched.

The split points came from the bundle, by package (rendered bytes, before minifying):

| Candidate | KiB | Decision |
|---|---|---|
| `ag-charts-community` + `ag-charts-core` | 2,142 | on demand: only two surfaces draw a chart |
| `ag-grid-community` + `ag-grid-react` | 1,858 | on demand: only three surfaces show a grid |
| `ToolUp.Platform.Client`, of which the built-in admin surfaces about 700 | 1,651 | stays: a surface's definition (name, icon, route, visibility) and its `update` are read at boot, so a split needs a deferred module form; a change to every surface for under a fifth of what remains |
| `ToolUp.Platform.Core` (decoders, wire types) | 499 | stays: the shell's boot calls reach it |
| `react-dom`, `react`, `scheduler` | 475 | stays: a first screen needs them |
| `fable-library`, `Fable.SimpleJson`, `Fable.Parsimmon` | 298 | stays: everything above uses them |
| `@dnd-kit/*` | 111 | stays: the sidebar's reordering, on every screen |

Real Edge, the same page in both arms, 15 alternating loads per cell, median milliseconds from `import()` to
resolved (2026-10-31, 16 logical cores). Transfer on a cold load is 1,024,774 bytes before and 366,326 after;
warm is 0 in both, which is what confirms each cell did what it says. The last column loads every chunk too, which
is what a page with a grid and a chart costs.

| Profile | Cache | Before | After | Change | After, all chunks |
|---|---|---|---|---|---|
| desktop, no throttle | cold | 201 | 73 | -64% | 151 |
| desktop, no throttle | warm | 200 | 71 | -65% | 144 |
| mid laptop: 4x CPU, 10 Mbps, 40 ms | cold | 1,514 | 566 | -63% | 1,384 |
| mid laptop: 4x CPU, 10 Mbps, 40 ms | warm | 660 | 233 | -65% | 463 |
| low phone: 6x CPU, 1.6 Mbps, 150 ms | cold | 6,354 | 2,335 | -63% | 6,141 |
| low phone: 6x CPU, 1.6 Mbps, 150 ms | warm | 1,079 | 393 | -64% | 777 |

Measured 2026-09-29 against Phase 884's tree: the minimal sample 2,727,355 bytes (790,119 gzipped),
the SDK shell 3,944,367 bytes (1,058,664 gzipped). The same phase measured building the generated
module's response decoders on first use rather than at import, and found nothing to buy — the
figures, and why the decoders stay built at import, are beside the emitter in
`ToolUp.Remoting.Generator`.

### The Feliz census: a count that may only fall

The client tier is moving off Feliz to typed trees, one project at a time. The risk in a migration that
long is stalling half done with two idioms in the tree, so it has a number, and the number may only
fall. [`feliz-census.json`](../../feliz-census.json) at the repository root holds it, cell by cell:

| Class | One cell per | Counts | May it rise? |
|---|---|---|---|
| `sources` | project | non-test `.fs` / `.fsx` files that depend on Feliz | no |
| `tests` | project | the same, over test files | yes, recorded |
| `baselines` | assembly | lines of `api-baselines/<assembly>.approved.txt` naming `ReactElement` | no |

**What it counts.** A file counts once, however much Feliz it uses, when its code depends on the React
DSL itself: a line `open Feliz`, or a qualified name such as `Feliz.Html.div`, `Feliz.prop.className`,
`[<Feliz.ReactComponent>]` or `open type Feliz.prop`. Its cell is the nearest directory above it holding a `.fsproj`. A baseline line
naming `ReactElement` is public surface a consumer's code is written against, so a view that leaves
Feliz but still hands out a `ReactElement` has not left it.

**What it does not count.** The namespaces of the other packages that share the prefix, opened or
qualified: `Feliz.AgGrid` (and its Enterprise companion), `Feliz.AgCharts`, their runtime modules and
`Feliz.ViewEngine`, which have their own exits; a mention in a comment or a string literal, which is not
code; a `namespace` or `module` declaration line, which is the file naming itself; build output and tooling directories (`bin`, `obj`,
`node_modules`, `fable_modules`, `output`, `artifacts`, any directory starting with `.`); and the
documentation fragments under `docs-snippets/`, which the documentation gates hold as prose. A test file
(a path segment named `tests`, or a project directory ending `.Tests`) is counted in `tests`, apart from
the code it tests: a view's tests move with the view, so that count may rise while a migration is in
flight, and is recorded rather than refused.

**The gate.** The `Build` test pack, which `VerifyAll` runs in every lane, holds the committed census
against the tree. It is red in both directions. A cell that rose is new Feliz, refused: remove it rather
than raise the number. A cell that fell is a census that does not record the fall: rewrite it in the same
change, so every decrease is a line in a reviewed diff and the number never drifts down unnoticed. A
`tests` cell that rose is a stale census too, and the writer records it.

```powershell
pwsh ./dev-scripts/feliz-ratchet.ps1           # the gate's check, on its own
pwsh ./dev-scripts/feliz-ratchet.ps1 -Write    # rewrite the census from the tree
```

Both run the build driver's `FelizRatchet` target (`dotnet run --project Build.fsproj -- FelizRatchet
[--write]`). The writer refuses, and writes nothing, when it would raise a `sources` or `baselines`
cell beyond what an allowance admits. The one way up is an **allowance**: an entry in the census's
`allowances` list, added by hand and reviewed, with its date, its reason and the cells and amounts it
admits. The next write admits a rise up to those amounts and marks every allowance `spent`, so it
admits nothing again; the gate is red while an allowance sits unspent, so none can be staged for a
later rise. The first census, written 2026-11-20, was 145 source files, 29 test files and 320 baseline
lines. When the migration ends, the gate becomes a ban.

**The counting rule widened on 2026-11-26.** Until then a file counted only when it had an `open Feliz`
line, so a file reaching Feliz by qualified name alone (the tree views' kept islands, a component
attribute) was invisible, and a file could leave the count by rewriting its `open` line. The rule now
counts a dependency either way, once per file, and strips comments and strings before it looks. The
census rose once, from 144 source files, 31 test files and 307 baseline lines to 147, 38 and 307: three
client files that were already in the tree, admitted by the census's first allowance and spent by the
same rewrite. No Feliz was new; after the rewrite, a rise is refused exactly as before.

---

## How refusals surface

1. **To the caller, as a typed value.** A budget refusal is a `BudgetDenial` in the error channel,
   never an exception, and — on the compute path — that same denial, in the `compute-budget`
   domain, projected onto the dispatcher's `ExternalComputeError` as a **terminal, non-retriable** failure. Terminal is the
   point: re-submitting an identical over-budget request cannot succeed, because the allowance does
   not refill on the timescale a retry loop runs at, so a caller that retried would turn one refusal
   into a hot loop against a budget that is by definition already exhausted.
2. **To an operator, as audit rows.** The compute path records `ComputeBudgetDenied` (carrying the
   denial's figures as `ComputeBudgetDeniedFigures`, the row shape the audit archives already hold,
   plus which surface refused and what was submitted) and
   `ComputeBudgetWarning` (once per scope per period). Both are ordinary `AuditEvent` cases, so they
   reach every composed audit sink and are queryable per scope.
3. **To a log, once.** A denial writes one warn-level line naming the scope, the dimension, the two
   numbers and the period. It is a supplement to the audit row, never the record.
4. **To metering, on settle.** A settled run records its actual cost through `IUsageLog` alongside
   the period key, so spend reconciles against the same accounting substrate as everything else. A
   *refused* run meters nothing — nothing was consumed.

A request refused by a budget **never reaches the inner substrate**: the payload does not leave the
process, and no backend is asked to start work the deployment cannot pay for. A check performed
after the backend accepted the work is a check on something that has already left.

### The two AI budgets

Both live in the AI tier (`ToolUp.AI.Core` / `ToolUp.AI.Server`) rather than here, and both are
`BudgetClaim`s checked by `BudgetPolicy.verdict` — so the seam stays currency-free (GP 1) while the
thing that turns a request into a *number* stays where a provider and a model are meaningful.

Their consumption is **derived, not reserved**. Nobody can know a turn's cost before the model
answers, so there is nothing honest to hold at admission: the window is summed from the always-on
per-turn telemetry stream, which since Phase 6i.B carries all four token counts and the serving
provider and model. The pre-call figure is therefore only the claim's `Requested` half — an estimate
— while `Spent` is always what providers actually reported. That is what "reserve an estimate,
settle the actual" means on a derived-consumption domain, and it is why neither uses `IBudgetLedger`.

Money needs one thing tokens do not: a **rate card**. `ModelPriceTable` maps `(providerId, model)` to
four per-million rates — fresh input, cached input, output, cache writes — under an operator-supplied
currency tag, and is registered through `ComposeExtensions.ServiceConfig`:

```fsharp skip=fragment
let rates =
    ModelPriceTable.ofList "USD" [ "acme", "acme-1", ModelPrice.simple 3.00M 15.00M ]

{ ComposeExtensions.empty with ServiceConfig = Some(fun s -> s.AddSingleton<ModelPriceTable>(rates)) }
```

No rate card ships in the SDK, for a reason worth stating: a vendor's price list embedded in a
vendor-neutral SDK is wrong within a quarter and bills an operator at a number nobody in their
organisation chose. A `(providerId, model)` the card does not name is **unpriced** — its turns
contribute nothing to the window, the pair is reported once through the logger and permanently on the
`/dev/inspect` panel, and nothing is ever refused on account of it (GP 11). An entry declaring zero
rates is a different fact: that is a *price* of zero.

The registered card's presence is also the composition gate. A deployment that registers none
resolves the object graph it always did — no decorator, no config read, no allocation (GP 13).

Both ceilings are set per scope in the `_platform.ai.budget` config tab: a token cap per user per
hour, and monetary caps per user and per scope, each with its own window drawn from `BudgetPeriod`.
Every one defaults to `0`, which the seam reads as unrestricted.

**A read that fails is decided by the declared `BudgetPosture`.** Each check reads two things before
it decides: the scope's policy from the config store, and the window from the telemetry stream.
When either read fails, `AIOptions.BudgetPosture` decides the call — `FailOpen` (the default, the
behaviour every deployment had before the posture existed) admits it, `FailClosed` refuses it with a
`BudgetDenial` whose dimension names the read (`unreadable-policy` / `unreadable-window`). Either way
the failure is recorded as one `BudgetWindowUnreadable` event under its own source module, and the
`/dev/inspect` panels show the posture and the last unreadable time beside the cached figures, so a
budget that is unbounded and one that could not be read never look alike:

```fsharp skip=fragment
AIOptions.create factory profile |> AIOptions.withBudgetPosture BudgetPosture.FailClosed
```

---

## See also

- [`external-compute.md`](external-compute.md) — `IExternalComputeDispatcher`, the seam compute
  budgets decorate, and the submit / poll / cancel handle model a reservation is settled against.
- [`model-fit-worker-contract.md`](model-fit-worker-contract.md) — the second enforcement point: a
  federated peer's fit submission never touches `Submit`, so budgeting only the dispatcher would
  leave it ungoverned.
- [`portability-rules.md`](portability-rules.md) — the six rules `IBudgetLedger` is audited against
  in its file header.
- [`events.md`](events.md) — the audit substrate the budget rows ride.
