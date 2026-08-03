# Fan-out concurrency bound — `FanoutPolicy.MaxConcurrency`

**Ships in:** `InterPlatform` (`FanoutPolicy.MaxConcurrency`, `FanoutPolicy.withMaxConcurrency`,
semaphore-gated launch in `DefaultPeerFanout`).

## What changes

`FanoutPolicy` modelled `Timeout` / `Quorum` / `CancelOnFirstSuccess` but carried no
max-parallelism, and `DefaultPeerFanout.Fanout` `Async.Start`ed every target at once. Over a large
federation graph that is a thundering herd — connection-pool exhaustion and downstream overload —
with no operator lever. This adds the lever as policy data (GP 12 rule 3), not a callback:

```fsharp
type FanoutPolicy = {
    Timeout: TimeSpan option
    Quorum: int option
    CancelOnFirstSuccess: bool
    MaxConcurrency: int option    // NEW — default None (unbounded)
}

// New builder. Unlike withTimeout / quorum / firstSuccess it takes the policy it
// refines, so a fan-out can bound parallelism AND early-return.
FanoutPolicy.withMaxConcurrency : int -> FanoutPolicy -> FanoutPolicy
```

### Behaviour

- `None` (the default, and what every existing builder produces) allocates no gate and launches
  every target at once — byte-for-byte the prior behaviour (GP 11 / GP 13).
- `Some k` is clamped to `[1, targetCount]` at fan-out time, exactly like `Quorum`. `0`/negative
  clamps to 1 (serialised) rather than deadlocking; a `k` above the target count degrades to
  unbounded.
- Under a bound, a child holds a `SemaphoreSlim` slot for the duration of its call, so at most `k`
  peer calls are ever on the wire.
- **The total-result-map and early-return contracts are unchanged.** A peer still queued behind the
  bound when a quorum / first-success / deadline fires is cancelled before it launches, writes no
  slot, and lands in the map as the same descriptive not-awaited `Error` a cut-short call produces.
  The caller can still distinguish answered-ok / answered-error / not-awaited.
- The bound composes with all three early-return modes.

## Diff to apply

**Nothing for existing consumers** — additive with a `None` default. Every consumer using
`FanoutPolicy.all` / `withTimeout` / `quorum` / `firstSuccess` is unaffected. Per the workspace
mandate the SDK-adoption matrix (`ToolUp/SDK-ADOPTION.md`) is a **generated** projection over the
consumers' own `sdk-adoption.json` manifests; since no consumer action is required here, this phase
is not marked `consumer_facing` and no row is hand-authored.

The one shape that needs a one-line edit is a consumer that constructs `FanoutPolicy` as a **record
literal** rather than through a builder (F# requires every field):

```fsharp
// Before
let policy = { Timeout = Some (TimeSpan.FromSeconds 5.0); Quorum = None; CancelOnFirstSuccess = false }

// After
let policy = {
    Timeout = Some(TimeSpan.FromSeconds 5.0)
    Quorum = None
    CancelOnFirstSuccess = false
    MaxConcurrency = None          // or Some k to bound in-flight calls
}
```

Preferred form — start from a builder and refine, which needs no edit when the record grows again:

```fsharp
// Bound alone
let bounded = FanoutPolicy.all |> FanoutPolicy.withMaxConcurrency 8

// Bound + early return
let boundedQuorum = FanoutPolicy.quorum 3 |> FanoutPolicy.withMaxConcurrency 8
let boundedDeadline = FanoutPolicy.withTimeout (TimeSpan.FromSeconds 5.0) |> FanoutPolicy.withMaxConcurrency 8
```

## Verification

- `dotnet fantomas src/InterPlatform/Server/PeerFanout.fs` — clean.
- `dotnet build ToolUp.Forge.sln` — clean, 0 errors.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — 5,305 passed,
  0 failed. Five new cases in the `Phase 18c IPeerFanout` list: the bound caps observed in-flight
  calls at 2 over 6 targets (counting stub) and still returns a total map; the unbounded default
  demonstrably exceeds that peak (the negative control that keeps the bounded assertion honest);
  the bound composes with `quorum` and the queued peers read as not-awaited, never `Ok`; a bound
  above the target count degrades to unbounded; a non-positive bound clamps to 1 rather than
  deadlocking.
- `api-baselines/InterPlatform.approved.txt` regenerated in the same commit — the record's
  compiler-generated `.ctor` gains a fourth parameter (the Phase 175 approval test reports that as
  a retype), plus the additive `MaxConcurrency` property and `withMaxConcurrency`.
- Server-only companion, no Fable tier to re-verify.

## Rollback

Additive throughout — revert the change. No deployment that leaves `MaxConcurrency = None` (every
existing one) is affected. A deployment that adopted the bound reverts to unbounded fan-out; drop
the `withMaxConcurrency` call and, if it constructed the record literally, the `MaxConcurrency`
field.
