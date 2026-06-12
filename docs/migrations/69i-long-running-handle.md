# Phase 69i — long-running operations via typed `JobHandle<'T>`

> **Substrate status: v0 shipped.** `JobHandle<'T>` / `JobStatus<'T>` and the `IJobDispatcher` seam with its in-memory default are live (`Server/Remoting/Jobs.fs` + `Types.fs`). In v0 the *handler* drives the dispatcher explicitly and the polling companion is hand-wired; planned follow-ups — dispatcher auto-recognition of the `Async<JobHandle<'T>>` return shape, an auto-generated `GetStatus` companion (via the 69k generator), SSE progress streaming (via 69c), and idempotency-key→job-id dedup (via 69f) — have not landed. This recipe describes v0 and will be updated as those ship.

## When to opt in

Any method whose work outlives a sensible request window — report generation, batch ingest, model inference. The hand-rolled alternative ("enqueue, return a `string` job id, client polls a bespoke status endpoint, casts the result at the call site") reinvents the polling protocol with subtle per-module variations. `JobHandle<'T>` makes the handle and the status **typed end-to-end**: polling a `JobHandle<ReportFile>` is guaranteed a `JobStatus<ReportFile>`.

## What changes

Two types and one seam, all additive:

- `JobHandle<'T>` — an opaque typed handle carrying the job id; the phantom `'T` gives the client compile-time safety.
- `JobStatus<'T>` — `Queued | Running of progress | Succeeded of 'T | Failed of message | Cancelled`.
- `IJobDispatcher` — `Enqueue<'T>: Async<'T> -> Async<JobHandle<'T>>` + `GetStatus<'T>: JobHandle<'T> -> Async<JobStatus<'T>>`. The shipped `InMemoryJobDispatcher` runs work via `Async.Start` and tracks status in-process.

Nothing is composed by default and methods that don't use the shape are untouched (GP 11 / GP 13).

## Diff to apply

```fsharp
// API record — the long-op returns a typed handle; the polling
// companion is hand-wired in v0 (auto-generation arrives with 69k):
type ReportsApi = {
    StartReport: ReportSpec -> Async<JobHandle<ReportFile>>
    GetReportStatus: JobHandle<ReportFile> -> Async<JobStatus<ReportFile>>
}

// Implementation — share one dispatcher instance across both methods:
let jobs: IJobDispatcher = InMemoryJobDispatcher()

let reportsApi = {
    StartReport =
        fun spec -> async {
            let work = async {
                let! file = computeReport spec
                return file
            }
            return! jobs.Enqueue work
        }
    GetReportStatus = fun handle -> jobs.GetStatus handle
}
```

The client starts the operation, holds the handle, and polls `GetReportStatus` until a terminal status (`Succeeded` / `Failed` / `Cancelled`) arrives — no casts, no bespoke status enum.

**`InMemoryJobDispatcher` limits (dev / single-instance):** restarts wipe job state; live (`Queued` / `Running`) jobs are never evicted, and when the tracked-job cap (default 100 000) is reached with every slot live, `Enqueue` fails loudly with a saturation error rather than silently dropping jobs. Production deployments with durability needs implement `IJobDispatcher` over a persistent backing store — the interface is two methods and honours the six portability rules.

## Verification

1. `dotnet build` — clean.
2. Call the start method: a handle returns immediately (sub-second), before the work completes.
3. Poll the status companion: observe `Queued`/`Running`, then `Succeeded` carrying the typed result.
4. A handler that throws surfaces as `Failed` with the exception message — the poll never hangs.
5. Polling an unknown handle (e.g. after a restart of the in-memory dispatcher) returns `Failed "job-not-found"`, not an exception.

## Rollback

Additive — revert the method signatures to their previous synchronous shape and drop the dispatcher instance. Methods not using the shape were never affected.

## See also

- [69-family-overview.md](69-family-overview.md) — family map and adoption sequence.
- [69f-idempotency-keys.md](69f-idempotency-keys.md) — duplicate-submission suppression for the start method until key→job-id dedup lands.
- [69k-source-generator-dispatcher.md](69k-source-generator-dispatcher.md) — the generator that will auto-emit the polling companion.
- Substrate: `src/ToolUp.Platform.Server/Server/Remoting/Jobs.fs`.
