# VerifyAll machine-wide serialisation + corpus-anchor process-level cache

**Status:** additive; **no consumer-facing surface**. Nothing in any published `ToolUp.*` package
changes — the `VerifyAll` target's body (`ToolUp.Platform.Build`, frozen at 0.23.0) is untouched.
What changes is how forge's OWN gate behaves when two of it run at once on one machine, and how the
Platform pack's test-support corpus anchoring pays for `git`.

## What changes

**1. `VerifyAll` serialises itself, machine-wide, per repository.** Before the target runs, the FAKE
driver (`Build.fs` → `VerifyGate.around`) takes a **named OS mutex** keyed on
`git rev-parse --git-common-dir`, so every worktree of one clone contends for ONE gate. A second run
waits — printing a progress line naming the holder every 30 s — instead of thrashing the first. On
2026-08-27 three to four concurrent Platform-pack runs turned a ~17-minute gate into more than two
hours; the workaround then was a lock-directory convention written into orchestration briefs. The
gate now owns its serialisation.

**2. `CorpusAnchor` resolves once per process.** `mainWorkingTree` and `resolve` are memoised per
checkout root (thread-safe: `ConcurrentDictionary<_, Lazy<_>>`, keyed the way the host compares
paths), and the module counts its `git` spawns (`gitSpawnCount`). A full Platform-pack run performs a
small constant number of spawns however many tests ask for an anchoring — and a test in the Build
pack now holds that property, so a future caller written without a `lazy` cannot reintroduce the
per-lookup cost.

## The gate — what takes it, what does not

| Run | Gated? | Why |
|---|---|---|
| `dotnet run --project Build.fsproj -- VerifyAll` (any lane: `pure` / `fast` / `full`) | **yes** | every lane runs the Platform pack; the `fast` lane drops its slow set, not the pack. A lane that runs seconds waits seconds |
| `pwsh ./verify.ps1` (which runs the above as its last step) | **yes** | the format check, header check and build steps before it pass through; the `VerifyAll` step takes the gate — and because the target builds the solution first (`buildOnce`), the solution build is inside the gate too |
| `roadmapctl dispatch gate run …` (the campaign's citable run) | **yes**, and also queued | the dispatcher's queue decides WHICH citable run goes next; the mutex makes sure no uncitable run is chewing the same CPU and memory while it does. The two compose: a queued run that reaches the front takes the mutex immediately unless an ad-hoc run holds it |
| Every other FAKE target (`Build`, `Format`, `AddHeaders --check`, `GenerateSdkManifest`, `VerifySemVerBump`, `VerifyFable`, `VerifyTemplates`, `VerifyPublishedPackages`, …) | no | not part of the measured contention; the CI-only verify targets are not in the gate script at all |
| A single pack run directly — `dotnet <pack>.dll --filter …` | no | never passes through the driver; a filtered single-pack run is the inner loop and stays free |

Widening the set is one edit to `gatedTargets` in `VerifyGate.fs`.

### Keying

The key is the **repository**, not the machine: `git rev-parse --git-common-dir` names the main
repository's `.git` from any of its worktrees, so `wt\441`, `wt\687` and `wt\735` of one clone agree
on one key (the estate runs campaigns as worktrees, which is the case the 2026-08-27 numbers were
measured on). Two SEPARATE clones on one machine resolve different keys and are not serialised
against each other. Outside a git repository the key degrades to the working directory itself and the
gate says so. On Windows the mutex is in the `Global\` namespace so two logon sessions share it.

### What you will see

```text
VerifyAll gate: another VerifyAll holds it (pid 41216 started 2026-09-17T08:12:03Z in C:\repos\Fuaran-ToolUp\wt\441) — waiting up to 60:00. Set TOOLUP_VERIFY_NO_GATE_MUTEX=1 to run unserialised.
VerifyAll gate: still held (pid 41216 started 2026-09-17T08:12:03Z in C:\repos\Fuaran-ToolUp\wt\441) — waited 0:30 of 60:00.
…
VerifyAll gate: acquired after waiting 14:20.
```

The holder line comes from a best-effort **holder note** at `<common git dir>/toolup-verify-gate/holder`
(pid, working directory, UTC start). It is diagnostic only — the mutex is the gate; a note that could
not be written or read costs the description, never the serialisation. A run started before this
phase, or one whose note failed, reports as `no holder note`.

### Bounds and escapes

| Knob | Default | Meaning |
|---|---|---|
| `TOOLUP_VERIFY_GATE_WAIT_MINUTES` | `60` | the stale-HOLDER bound. Past it the run **proceeds unserialised, loudly** — the pre-phase behaviour is the floor, and a gate that failed because it was guarding against slowness would have traded a slow run for no run. A value that is not a positive whole number is ignored with the default in its place (a typo that read as zero would be the opt-out spelled wrong) |
| `TOOLUP_VERIFY_NO_GATE_MUTEX` | unset | `1` (any non-empty value other than `0`) skips the gate entirely. For CI runners, which are isolated and must never wait on a phantom holder, and for anyone paying the contention deliberately |

A holder that **dies** does not need the bound: the kernel releases an OS mutex when its owner exits,
the waiter is handed it as abandoned, and the gate reports `the holder exited without releasing it … taking it
over`. The bound exists for a holder that is alive and stuck. If the platform refuses the
mutex (an access-control difference between two users), the run proceeds unserialised naming the
reason (`… — proceeding WITHOUT serialisation.`). Past the bound the line is `waited 60:00 and the
holder has not released it — treating it as STALE and proceeding UNSERIALISED`.

The default of 60 minutes is derived: comfortably past a full lane's ~16 minutes plus two advisory
lanes that might be queued ahead of it, and short of the dispatcher queue's own 90-minute claim bound,
so the queue's stale-claim handling never fires because of this gate.

## Why the gate lives at the repo root, FAKE-free

The `VerifyAll` target's body is published SDK surface. The gate is forge's own build hygiene, so —
like `SdkManifest.fs`, `SemVerBump.fs` and `TestLane.fs` — `VerifyGate.fs` sits beside `Build.fs`,
is compiled into `Build.fsproj`, and is **source-linked** into `ToolUp.Platform.Build.Tests` so the
driver and its go-red proofs run one implementation. It takes nothing beyond `System`. The seam the
tests use (`aroundWith`, over a mutex name the test mints) is what lets the pack exercise the
admission rules without contending for the gate the `VerifyAll` that is running it holds.

## A finding the measurement produced

The phase shard framed the contention as CPU thrash among concurrent Platform-pack runs. While this
phase was in flight, three worker sessions plus a gate exhausted a 54 GB commit limit three times and
a solo probe run died at 253 s inside `buildOnce` with `MSB4166` (child node exited prematurely — the
build coordinator's out-of-memory shape). **Concurrent `dotnet build` of the solution is a memory
contention source alongside the pack's CPU contention**, and the gate is taken AROUND the whole
target — the build-once step included — precisely so it covers both. The before-vs-after wall-clock
comparison the shard asked for could not be run on this machine under those conditions and is
recorded as deferred on the phase's outcome; the gate's own admission log lines, and the dispatcher's
`gate-<phase>.json` records, are where the numbers accrue from here.

## Verification

- `dotnet build src/ToolUp.Platform.Build.Tests/ToolUp.Platform.Build.Tests.fsproj` then run the pack
  (`dotnet <bin>/ToolUp.Platform.Build.Tests.dll`) — `VerifyGateTests` pins free / held / stale-holder
  / abandoned / opt-out admission and bound parsing, each with its falsifier; `CorpusAnchorTests` pins
  the constant-spawn property.
- Hold the gate by hand and watch the driver wait:

  ```powershell
  # in one shell — take the mutex the driver derives for this clone
  $key = # first 16 hex of SHA-256 over the lower-cased, normalised common git dir
  $m = New-Object System.Threading.Mutex($true, "Global\ToolUp.VerifyGate.$key")
  # in another
  dotnet run --project Build.fsproj -- VerifyAll     # prints "another VerifyAll holds it … waiting"
  $env:TOOLUP_VERIFY_NO_GATE_MUTEX = 1
  dotnet run --project Build.fsproj -- VerifyAll     # prints "running without the gate" and starts
  ```

  Both directions were observed on 2026-09-17 (the driver waited under the held mutex; with the
  opt-out set it reached `Starting target 'VerifyAll'` within 6 s).
- `TOOLUP_CORPUS_ANCHOR_TRACE=1` makes a test process print its total `git` spawn count on exit.

## Rollback

Set `TOOLUP_VERIFY_NO_GATE_MUTEX=1` — the driver runs exactly as before this phase. To remove the gate
outright, delete the `VerifyGate.around` wrap at the end of `Build.fs`'s `main` and the two
`VerifyGate.fs` compile entries (`Build.fsproj`, `ToolUp.Platform.Build.Tests.fsproj`) with their
tests. The `CorpusAnchor` memoisation is inert to remove: drop the two caches and return the
uncached resolvers to their original names.

## See also

- `VerifyGate.fs` (repo root) — the design, the derivation of the bound, and every admission case.
- `src/ToolUp.Platform.Tests/Support/CorpusAnchor.fs` — the memoisation and its rationale.
- `docs/migrations/326-sdk-manifest-reconcile.md` — the precedent for a root, FAKE-free build-hygiene
  module source-linked into the Build pack.
