# Phase 849 — the browser runtime, measured

**What changes for you: nothing.** This is measurement substrate. No shipped runtime path changed,
and every deployed app is byte-for-byte identical. The one public-surface change is in
`ToolUp.Platform.Build` (the FAKE gate package): `PerfMetric` gained three client cases, and
`PerfBudgetGate` gained `parseClientBudget`, `verifyClient` and a `VerifyClientPerfBudget` target.
A consumer that pattern-matches `PerfMetric` exhaustively sees an incomplete-match warning and adds
the three cases; nothing else needs an edit. Read on if you maintain forge, if the client half of the
perf gate has gone red on your PR, or if you are working a client-runtime phase and need the before
number.

---

## What it is

Every performance number the repository held before this phase was a .NET number. The path a user
actually waits on — the transpiled Elmish loop, the remoting proxy's response decode, the boot that
builds proxies by reflection at module import — runs as JavaScript and had never been timed.
`ClientBench` (`src/ToolUp.AI.Client.Tests/ClientBench.fs`) times it under Node, on the code as Fable
transpiles it:

| Metric | What it measures | Ceiling | Baseline |
|---|---|---|---|
| `client.bootMs` | `import()` of the transpiled `samples/MinimalClient` to its first commit into `#elmish-app`, fresh process per sample | 17500 ms | 1265 ms |
| `client.decodePerResponseUs` | the proxy's 200 path — `SimpleJson.parseNative` + `Convert.fromJsonAs` — per response, over the wire corpus's `.json` fixtures | 80 us | 5.66 us |
| `client.viewPerDispatchUs` | the `view` the render hook builds for one dispatched model-changing message (a 200-row Feliz table) | 12500 us | 906 us |

The ceilings sit in the `client` block of `perf-budgets.json`, beside the server block, at Phase
192's slack ratio (~14x) and with a note each. `ToolUp.Platform.Build.Tests` refuses any client
ceiling more than 20x its baseline, and any ceiling without a note.

## How it runs

```powershell
# the whole gate: server half, then client half, then the teeth check over both
pwsh ./dev-scripts/perf-budget-gate.ps1 -TeethCheck

# the harness alone (after a Fable compile of src/ToolUp.AI.Client.Tests)
cd src/ToolUp.AI.Client.Tests
$env:NODE_ENV = 'production'
node --import ./register-loader.mjs output/Program.js ClientBench --seed 849001
```

- **Not in the ordinary test run.** `Program.fs` enters the bench only when the argument `ClientBench`
  is present; `node --test` passes none, so `VerifyFable` and the `fable-tier` job are unchanged.
- **Reproducible by seed.** Fixture order and the dispatch sequence come from the proof corpus's LCG
  (`ElmishProofDifferential.Lcg`); the seed is printed beside the runtime on every run.
- **React's production build, enforced.** React picks its build from `NODE_ENV` at first import, and
  the development build validates every element — a view number taken under it is several times the
  production one. The harness refuses boot and view samples taken without `NODE_ENV=production`
  (they are written `observed: false` and the decider refuses them); the gate script sets it.
- **Every decode is checked before it is timed.** A fixture that does not decode to its declared
  value through the proxy's path is excluded by name, with its reason, on every run (two do today —
  see below). A leg that measured fewer than 20 fixtures is refused as unobserved.
- **CI:** the same `perf-budget` job as Phase 192, now with Node set up. It is **still not a required
  check**: Node is not a browser, the runner is shared, and these numbers are first of all the
  before/after instrument for the client-runtime phases (851 onward). Promote it to required only once
  a few weeks of runner history show the ceilings do not flake.

### What transfers to a browser, and what does not

| Measurement | Transfers unchanged? | Why |
|---|---|---|
| decode per response | **yes** | the same JavaScript, the same `JSON.parse`, the same reflective walk |
| view construction per dispatch | **yes** | the same F#-to-JS `view`, building the same React elements; React's commit is not in the number |
| boot | **import yes, paint no** | module evaluation, `init`, `view` and React's commit are the browser's; jsdom lays nothing out, and the sample's AG Charts gallery draws into a no-op canvas stand-in (jsdom has no canvas, and without the stand-in the chart throws and React 19 unmounts the whole root) |
| the async command hop | **mechanism yes, magnitude no** | it is a `setTimeout` in both hosts, but the timer's granularity is the host's |

## 849.D — what the first runs measured, lever by lever

Measured 2026-09-27, node v25.9.0, win32/x64, React production build, seed 849001, on a machine
running a concurrent build campaign (load average 100% throughout — so these overstate a quiet
machine, the way Phase 192's loaded run did). Minimums unless stated; per-run figures are in the
budget's notes. Each later phase cites these as its **before**.

### Proxies built at boot (Phase 853)

| Import graph | Proxies built during it | Import time (min) |
|---|---|---|
| `samples/MinimalClient` → first render | **0** — the remoting module never loads | 1265 ms to first render |
| the SDK shell's module graph (`ToolUp.Platform.Client` `SDK.Client`, no render) | **45** | 1341 ms |

The proxy count is exact, not sampled: the boot child wraps the transpiled `Remoting.buildProxy` —
the one function every `Api.makeProxy` reaches — in a counter (`client-bench-loader.mjs`), and a
counter it cannot install is reported as UNOBSERVED, never as zero.

**Premise correction for 853:** its shard cites "~78 `Api.makeProxy` sites … all of it runs during
boot". The shell's own graph builds **45** at import; the rest sit in companion client tiers
(AI, KnowledgeBase, …) and are paid only by an app that imports them. And the minimal client builds
**none**, because it does not reach the shell at all — so 853's boot win is measured on the shell
import leg, not on `bootMs`.

### Decode cost per response against `JSON.parse` (Phase 843)

Over the 72 cross-host corpus fixtures that decode to their declared value:

| Path | Per response |
|---|---|
| `JSON.parse` alone | 0.58 us |
| `SimpleJson.parseNative` (JSON.parse + the `Json` tree) | 1.42 us |
| **the proxy's path** (`parseNative` + `Convert.fromJsonAs`) | **5.66 us** |

About three-quarters of a response's decode is the reflective `Convert.fromJsonAs` walk. Primitives
cost ~1.1x `JSON.parse`; the structured shapes are where it goes — `record-envelope` 37.9 us
(10.2x), `binary-past-bin8` 25.0 us (46x — a byte array arrives as base64 and is decoded element by
element), `record-consignment` 15.1 us, `recursive-tree` 14.6 us, `record-nested` 12.7 us,
`map-union-key` 10.5 us, `union-multifield` 6.9 us (8.9x).

Two fixtures are **excluded** because the proxy's path does not decode them to their declared value
— both are findings for 843's decoder rather than bench defects:

- `decimal-max` decodes to `79228162514264340000000000000` against `79228162514264337593543950335`
  (the value passes through a JS number and loses precision);
- `date-timespan-negative` — the corpus's comparison reports `decoded -1, but the case declares 0`.

### Render-hook calls per drain (Phase 851)

One drain of 17 messages (one dispatched message whose `update` returns 16 `Cmd.ofMsg`) calls the
render hook **17 times** — once per `update`, as `Program.runWithDispatch`'s step order says. So a
drain of N messages builds the view N times; at the 200-row view's **906 us** per build, a 17-message
drain spends ~15 ms building views of which the user sees one.

### The 1 ms timer hop before an async command (Phase 851)

From an `update` issuing `Cmd.OfAsync.perform` to the async body starting: **min 1.12 ms, median
14.6 ms, max 20.7 ms** (n=20). The minimum is the `Timer.delay 1` hop itself; the median is this
host's timer granularity (Windows' default tick is ~15.6 ms) rounding that 1 ms up. A browser applies
its own clamping, so the magnitude does not transfer — the hop does: it is a macrotask every async
command waits behind. Other runs on the same machine: min 1.65 / 1.87 / 3.12 ms, median 15.2 / 55.8
(heaviest load) / 12.1 ms.

## Rollback

Delete the `client` block from `perf-budgets.json` and pass `-SkipClient` to the gate script (or
revert the script); the server half is unchanged. The harness is inert unless invoked by name.
