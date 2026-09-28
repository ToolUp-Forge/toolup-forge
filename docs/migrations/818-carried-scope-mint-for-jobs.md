# A carried mint for jobs — the scope a job runs under is the scope its scheduler resolved

**Ships in:** ToolUp.Platform.Core (`JobContext.Scope`, the internal `ResolvedScope.ofCarried`),
ToolUp.Platform.Server (`IJobScheduler.Schedule(scope, registration)`, `InProcessJobScheduler`,
`QuotaGatedJobScheduler`), ToolUp.JobSchedulers.Quartz, ToolUp.Facts.Server (`RecomputeJobHandler`).

## What changes

Phase 797 made a request's scope a value only the platform's scope resolution can mint
(`ResolvedScope`), and kept the string members of the fact store for scopes the platform *carries*
rather than resolves. The job scheduler is the largest carrier: a registration names its scope as a
string, the scheduler persists it, and the handler got it back as `JobContext.ScopeId` — nothing tied
that string to a scope any principal resolved.

- **`IJobScheduler.Schedule(scope: ResolvedScope, registration: JobRegistration)`** — a second
  `Schedule` overload. Same validation chain, same idempotency rule; the job is registered under
  `scope.ScopeId` (the registration's own `ScopeId` is ignored). The in-process scheduler records on
  the job definition that the scope was resolver-minted, together with the resolver's whole
  `StorageScope`.
- **`JobContext.Scope: ResolvedScope`** — the typed form of `ScopeId`. The in-process scheduler
  re-mints the persisted scope on every dispatch, across restarts, through a second **internal**
  mint (`ResolvedScope.ofCarried`, reachable from `ToolUp.Platform.Server` only). A job scheduled
  through the string `Schedule` runs under `ResolvedScope.anonymous` on this field — never a widening
  — and its `ScopeId` is unchanged, so a handler that reads only the string sees no difference.
- **The provenance rides the definition's `Tags`** under the reserved prefix `_platform.scope.`. The
  string `Schedule` strips that prefix from a caller's tags before persisting, so a registration
  (including one that arrived over the job-admin wire) cannot claim a provenance it lacks.
- **`RecomputeJobHandler`** reads and re-asserts through the fact store's `ResolvedScope` members
  when the job's scope was re-minted, and through the string members over the carried `ScopeId`
  otherwise — exactly as before.
- **No public constructor for `ResolvedScope` is added.** The Phase 797 reflection pins still hold,
  and are widened: the carried mint is internal, and a source guard pins that it has exactly one
  caller.

## What remains carried — and why

- **The reactive recompute path.** `ReactiveDataChange` enqueues a recompute from
  `IDataObjectStore.Save` / `Recover`, whose only scope is a `scopeId: string`; no resolved scope
  rides the call, and there is no ambient one. Minting a `ResolvedScope` there from that string
  would be the promotion the type exists to forbid, so the enqueue stays on the string `Schedule`
  and the handler keys the store on the carried `ScopeId`. Typing that hop needs the data-write
  origin typed first.
- **Schedulers outside the platform's server tier** (the Quartz companion, any third-party
  implementation) cannot re-mint — the constructor is internal — and hand their handlers the
  anonymous scope on `JobContext.Scope`. The contract pack's `carriedScopeTests` takes this as an
  explicit declaration (`remints = false`) and still pins "never a widening".
- **The job-admin API** (`JobApiHandler`) still schedules through the string overload.
- **The provenance is as trustworthy as the job store.** A party that can write job definitions
  straight into the store is inside the server's trust boundary already, and holds the fact store's
  string members besides.

## Diff to apply

This is a **breaking** change for two groups; callers of `Schedule` and handlers that read
`JobContext` are unaffected.

**Custom `IJobScheduler` implementations** must implement the new overload. Annotate the string
form's parameter so the two are unambiguous; a scheduler outside the platform's server tier forwards
to the string form (its handlers will see the anonymous scope):

```fsharp skip=fragment
// Before
interface IJobScheduler with
    member _.Schedule(registration) = ...

// After
interface IJobScheduler with
    member _.Schedule(registration: JobRegistration) = ...
    member this.Schedule(scope: ResolvedScope, registration: JobRegistration) =
        (this :> IJobScheduler).Schedule { registration with ScopeId = scope.ScopeId }
```

A **decorator** over another scheduler must forward the typed overload *as the typed overload*
(`inner.Schedule(scope, registration)`), or it silently drops the provenance.

**Code that constructs a `JobContext` literal** (test harnesses, a custom scheduler) adds the field:

```fsharp skip=fragment
let ctx: JobContext = {
    JobId = jobId
    ScopeId = scopeId
    Scope = ResolvedScope.anonymous // or the scope your scheduler re-minted
    // ...
}
```

**A handler that wants the typed scope** reads `ctx.Scope`. Treat an anonymous `ctx.Scope` on a job
whose `ctx.ScopeId` is not `"anonymous"` as a carried scope: key the store on `ctx.ScopeId`, as
`RecomputeJobHandler` does — reading the anonymous shard for it would find nothing.

**A request-path caller that holds a `ResolvedScope`** (from `ScopeResolution.forRequest`) and
schedules work for that scope should call the typed overload.

## Verification

- `dotnet build ToolUp.Forge.sln` — surfaces every custom scheduler and every `JobContext` literal.
- `dotnet run --project src/ToolUp.Platform.Tests -- --filter-test-list "Phase 818"` — the carried
  scope arms of the scheduler contract pack, against the in-process scheduler (re-mints) and the
  Quartz companion (cannot re-mint): the typed job runs under the resolved scope across a restart; a
  string-scheduled job, and one whose tags claim a provenance, runs anonymous with its `ScopeId`
  unchanged; a recompute job scheduled under a resolved scope reads and re-asserts through the
  typed store members.
- `dotnet run --project src/ToolUp.Platform.Tests -- --filter-test-list "Phase 797"` — the widened
  choke-point pack: the carried mint is internal, has one caller, and the recompute path mints
  nothing.

## Rollback

Revert the SDK version pin. Persisted job definitions keep the `_platform.scope.*` tags, which an
older scheduler stores and ignores as ordinary tags; nothing else persisted changes shape.
