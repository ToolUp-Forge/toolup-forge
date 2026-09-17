# Phase 766 — cron-tick single-leader election

**Consumer action required: none.** Additive. The behaviour change is visible only to a deployment
running more than one job-scheduler replica over one shared `IDistributedLock` — which previously
ran each due cron job once per replica, and now runs it once.

## What changes

| | Before | After |
|---|---|---|
| Lease acquisition for a `ScheduledByCron` dispatch | `DistributedLock.acquireBlocking` — the contended loser QUEUED behind the winner | `IDistributedLock.TryAcquire` — the contended loser SKIPS the tick (`cron_tick_skipped_contended`) |
| In-lease re-read before a cron dispatch runs | `Status = Active` and no outstanding `AwaitingExternal` run | the same, plus **still due for this tick** (`NextRunAt <= tickAt`); a job a sibling has already advanced is skipped (`cron_tick_skipped_not_due`) |
| Every other trigger source (manual, event, drift back-fill, external-retry continuation) | queue on `acquireBlocking` | unchanged — each is a request for THIS run, not for the occurrence the clock produced |
| `InProcessJobScheduler` public surface | `RunCatchUpScan`, `ReconcileAwaitingExternal` | **+ `RunTick(now: DateTime) : Async<unit>`** — one on-demand tick, as the hosted loop would run it at `now` |
| A single replica | — | byte-for-byte the same schedule: its tick never contends with itself, and a job it has just advanced is no longer due |

The two skip conditions and the reasoning are in
[technical-guide ch13 → The cron tick is an election](../../src/ToolUp.Platform/technical-guide/13-deployment-shapes.md#the-cron-tick-is-an-election-phase-766).

## Why

Phase 16a's audit found the cron due-job tick to be the one genuinely unleased cross-silo tick, and
its shape was worse than "unleased": two `WorkerOnly` replicas over one shared lease store both read
the same due job, the loser waited for the winner's lease, won it on release, re-read a job that was
`Active` with nothing outstanding, and ran it again. Ch13's "the work happens once" was an
understatement in the wrong direction. `CronTickElectionTests` pins the double-run as its own-lock-
table control and asserts exactly one run under a shared lease, one arm per skip condition.

## Diff to apply

**Every deployment:** nothing. No `ServerConfig` field, no compose entry point, no record shape
moves. The new member is additive on `InProcessJobScheduler`; the `ToolUp.Platform.Server`
api-baseline and its doc-coverage row are regenerated in the same commit.

**A deployment that hand-constructs the scheduler with a store-backed lock** (the only shape in
which two replicas share a lease store today — see the boundary below) gets the election with no
change: the `distributedLock` constructor argument is the seam it already used.

**Operational tooling / tests** may call `scheduler.RunTick DateTime.UtcNow` to run one tick on
demand. It returns once the dispatches have been STARTED, exactly as the hosted loop's tick does,
and records no drift — it is not a substitute for the hosted loop.

## The boundary this does NOT move

`compose` still constructs the job scheduler over `InProcessDistributedLock.shared` (the Phase 9c
follow-on documented in `SDK.Server.fs`), so under `compose` two replicas still each hold their own
lock table and the election runs on nothing shared. **`ReplicaCount = 1` on `WorkerOnly` stands**,
now for the lock wiring and the per-replica webhook queue rather than for the tick itself. Threading
a companion lock into the eagerly-composed subsystems needs the lock resolved before `compose` builds
them; that is a separate phase.

## Verification

```powershell
dotnet build src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
dotnet src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter "ToolUp.Platform.Tests.CronTickElection"
# 4 tests run — 4 passed. The CONTROL arm passes by observing TWO runs; the two shared-lease arms by observing one.
dotnet src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter "ToolUp.Platform.Tests.InProcessJobScheduler"
# the existing single-replica pack — unchanged, 40 passed.
```

In a multi-replica deployment over a shared lock, a contended tick now logs
`event=cron_tick_skipped_contended` or `event=cron_tick_skipped_not_due` on the loser at `Info`, and
the job's run history shows one `Succeeded` row per occurrence rather than one per replica.

## Rollback

Revert the commit. The election is two branches inside `JobScheduler.dispatchFrom` /
`dispatchHolding` with no persisted state of its own — a rollback restores the queue semantics and
the double-run with it. There is nothing to migrate back.
