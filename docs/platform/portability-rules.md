# Portability rules for distributed implementations

Many SDK interfaces (`IJobScheduler`, `IJobStore`, `IModuleQueryBus`, `INotificationChannel`, `IShareTokenStore`, `ISubjectResolver`, `IAnonymousSessionMigrator`, etc.) could plausibly be implemented by a distributed task framework — Akka.NET, Orleans, Hangfire, Redis-backed, etc. Doing so without breaking consumers requires that the **shape of the contract itself** stays portable.

The six rules below define that portability discipline. Any interface that *might* be implemented distributed-side **must** satisfy all six. The conformance test packs (`IJobSchedulerContract`, `IModuleQueryBusContract`, `IShareTokenStoreContract`, `IDataSourceContract`, `IEntityStoreContract`, etc.) are the executable enforcement bar — every implementation passes the same tests.

## Rule 1 — Identity by value

Returns and parameters use `string`, `Guid`, or domain records — never live framework handles (`IActorRef`, `IGrainReference`, `Task<T>` you can't resume from a different process, etc.).

```fsharp
// Good
abstract GetJob: jobId: string -> Async<JobDefinition option>

// Bad — Akka actor ref doesn't cross process boundaries
abstract GetJob: jobId: string -> Async<IActorRef>
```

**Why**: identity by value means the same caller can look up the same handle from any node in a cluster. Live handles bind to a process; if that process is gone, so is the handle.

`ISubjectResolver` honours rule 1 by returning a `Subject` record (the four-case DU) rather than a live identity handle — every node resolves the same `Subject` from the same `HttpContext` + claim stash. `IShareTokenStore.ListByIssuer` returns `ShareTokenClaim` records keyed by issuer userId, never an in-memory iterator that would bind to one process.

## Rule 2 — Async at every boundary

Every interface method returns `Async<T>` or `Task<T>`. Synchronous methods (`unit -> T`) and fire-and-forget `Tell`-style signatures (`unit -> unit`) are violations.

```fsharp
// Good
abstract Publish: scopeId: string -> Notification -> Async<unit>

// Bad — caller can't await the actual publish completion
abstract Publish: scopeId: string -> Notification -> unit
```

**Why**: `Async`/`Task` lets a distributed impl do real network I/O, batch, retry, and propagate failures. Sync signatures force a buffer-or-block decision the impl can't make safely.

### Documented exception: `IMetricsSink`

The metrics interface is sync-by-design — hot path, write-only, no return to await. The trade-off is documented; metrics sinks that need to do I/O buffer internally and flush on a background timer.

### Documented exception: compose-time-only methods

Methods invoked exclusively at compose time MAY return `unit` synchronously rather than `Async<unit>` when (a) the call site is known to be the composition root or its narrow delegates (typically `ServerModule.Server.fs` once per module), and (b) the interface header documents the compose-time-only contract. The in-process default for `IJobScheduler.RegisterHandler` follows this carve-out — its header explicitly notes "Modules typically call this once at compose time … the SDK does NOT call it per request", and a sync `unit` return reflects the pure-registration semantics (no I/O, no failure to propagate). A future distributed companion (Akka cluster, Orleans) registering handlers at runtime over the network would need to surface an additional `RegisterHandlerAsync : name -> IJobHandler -> Async<Result<unit, _>>` overload alongside the sync method; the sync overload stays for compose-time consumers and the async overload covers the runtime-registration path. This carve-out is parallel to the `IMetricsSink` sync exception above — both prefer a sync signature when the interface's documented usage pattern makes the async boundary noise rather than substrate.

## Rule 3 — Retry and supervision as data

Retry, backoff, and dead-letter behaviour are expressed as records (e.g. `RetryPolicy`). Callback parameters like `OnFailure: exn -> unit` or supervision-strategy objects leak framework semantics.

```fsharp
// Good
type JobRetryPolicy = {
    MaxAttempts: int
    BackoffSeconds: int list
    DeadLetterAfter: TimeSpan option
}

// Bad — exposes Akka-specific supervision semantics
abstract RegisterJob: jobId: string -> handler: ... -> supervisor: SupervisorStrategy -> Async<unit>
```

**Why**: data-shaped retry/supervision serialises across the wire, persists across restarts, and translates cleanly between frameworks. Callback supervision is framework-bound and untransportable.

`RateLimitConfig` follows the same data-not-callback shape — the per-`SubjectKind` policy map (`{ Default; PerShape: Map<SubjectKind, RateLimitPolicy> }`) is a serialisable record, not a function callback resolving a policy per request. A future distributed limiter (Redis-backed token bucket, etc.) consumes the same record and runs the same partitioning algorithm against the resolved `Subject`'s kind. See [`security.md`](security.md#per-shape-rate-limiting-guidance) for the per-shape partition-key contract.

## Rule 4 — Stateless handlers between invocations

Handler interfaces (`IJobHandler.Execute`, `IQueryHandler.Handle`, notification subscribers) receive all state via parameters (`JobContext`, payload, `AccessContext`). Implementations must NOT assume in-memory state between calls.

```fsharp
type IJobHandler =
    // Good — every input is on the parameter list
    abstract Execute: ctx: JobContext -> payload: byte[] -> Async<JobResult>
```

**Why**: Orleans can deactivate grains between calls; Akka.Persistence can restart actors after a crash. Anything cached in handler instance fields evaporates. Inputs flow on the parameter list; if a handler needs durable state, it persists through `IBlobStorage` / `IEntityStore` / etc.

`IAnonymousSessionMigrator.Migrate` honours this — the per-`userId` `SemaphoreSlim` lock that prevents the double-migration race lives in the SDK middleware, not in migrator instance state. Migrators receive the `AnonymousSessionId` + the resolved `AuthenticatedUser`'s userId per call and must be idempotent: re-running the same migration on already-migrated state returns success without side effects.

## Rule 5 — No cross-shard ordering promises

Documentation and tests make clear that ordering is guaranteed only within a `ShardKey`. A method whose correctness depends on cross-shard ordering is a violation.

For `IJobScheduler`: jobs with the same `JobId` execute in order. Across different `JobId`s no ordering promise exists — Job A's tick-at-09:00 may complete before or after Job B's tick-at-09:00.

For `IModuleQueryBus`: queries on the same shard key are ordered. Across shards no ordering.

**Why**: distributed implementations partition by shard key for horizontal scale; cross-shard ordering would force a single coordinator and serialise the whole system.

## Rule 6 — Precision at the lower bound

Scheduling and timing primitives declare their precision contract (`JobPrecision: Second | Minute`). An interface that implicitly promises sub-second precision where some implementations can't honour it (Orleans Reminders fire at minute granularity, for example) is a violation.

```fsharp
type JobPrecision = Second | Minute

type JobDefinition = {
    // …
    Precision: JobPrecision  // explicit
}
```

The default `InProcessJobScheduler` rejects `Second`-precision job registration with `ScheduleError.PrecisionNotSupported` rather than silently slipping to minute granularity. The decision is the caller's: ship a lower-precision job or use a different scheduler.

**Why**: precision drift is silent and impossible to debug. Explicit precision declaration forces callers to acknowledge the floor; tests can verify both impl + caller agree.

## Other portability constraints (corollaries)

The six rules are the load-bearing core. These follow from them:

- **No framework-specific serialisation attributes** (`[<Serializable>]`, `[<ProtoContract>]`, Akka `IWithUnboundedStash`, etc.) on any type in a shared `<Compile>` file. Wire format is JSON via `Fable.Remoting.Json.FableJsonConverter` for SDK SSE/persistence paths, vendor-specific for companion sinks.
- **No `open Akka.*` / `open Orleans.*`** in any file under `ToolUp.Platform.*`. Distributed implementations live in companion packages (`src/JobScheduler/Akka/`, etc.) — the SDK interface never references a companion's types.
- **Companion packages exist only at the SDK boundary.** The SDK interface never references a companion's types. Consumers pull both the SDK and the companion; the companion implements an SDK interface.

## Conformance bar

Every distributed-friendly interface has a contract test pack in `ToolUp.Platform.Tests`. External implementations consume the test pack:

```xml
<PackageReference Include="ToolUp.Platform.Tests" />
```

And run the pack against their impl in their own test suite. The pack tests behaviour, not implementation — the same N tests pass for `InProcessJobScheduler`, a future Akka.NET impl, and a future Orleans impl.

Current packs:
- `IJobSchedulerContract` (15 tests)
- `IModuleQueryBusContract` (9 tests)
- `IShareTokenStoreContract` (11 tests)
- `IDataSourceContract` (7 tests)
- `IEntityStoreContract` + `IEntityQueryContract` (22 tests combined)

A "distributed companion" not bundled with its conformance run is the wrong shape — without the test pack as evidence, the portability claim is unverified.

## How this interacts with companion authoring

If you're writing a new SDK interface destined to have distributed impls in the future:

1. Sketch the interface with all six rules in mind from day one. Retrofitting rule 2 (async at every boundary) is destructive.
2. Author the contract test pack alongside the interface, not after. The first in-process impl validates the contract; future impls pin to it.
3. Document the precision floor explicitly (rule 6) — what's the smallest tick you guarantee? Both impl and caller agree on the floor; tests verify.
4. If you find yourself wanting to expose a live handle (rule 1) or a callback (rule 3), find another shape. Live handles are a portability trap; callbacks are an Erlang-shaped design that doesn't survive the .NET runtime model.

If you're writing a companion impl of an existing distributed-friendly interface:

1. Run the conformance test pack against your impl in your test suite. If a test fails, your impl is wrong, not the test.
2. Document the precision floor your impl actually achieves (might be higher than the SDK floor — Orleans Reminders are minute-precision; an Akka impl might honour sub-second).
3. Distributed-ready impls must be stateless between handler calls (rule 4). In-process impls *may* hold state but document it clearly so consumers know not to use it distributed.
