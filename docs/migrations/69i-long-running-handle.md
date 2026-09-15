# Phase 69i — long-running operations via typed `JobHandle<'T>`

> **Substrate status: shipped.** `JobHandle<'T>` / `JobStatus<'T>` and the `IJobDispatcher` seam with its in-memory default are live (`Server/Remoting/Jobs.fs` + `Types.fs`), and since 69i.B–G the dispatcher **recognises** the `Async<JobHandle<'T>>` return shape at startup and **serves the poll / progress / cancel companions itself** when the dispatcher instance is composed (`Remoting.withJobDispatcher`). The consumer authors the start method and nothing else. Without the composition, v0's hand-wired shape still works unchanged (GP 11).

## When to opt in

Any method whose work outlives a sensible request window — report generation, batch ingest, model inference. The hand-rolled alternative ("enqueue, return a `string` job id, client polls a bespoke status endpoint, casts the result at the call site") reinvents the polling protocol with subtle per-module variations. `JobHandle<'T>` makes the handle and the status **typed end-to-end**: polling a `JobHandle<ReportFile>` is guaranteed a `JobStatus<ReportFile>`.

## What changes

Two types, one seam, and — since 69i.B — one composition:

- `JobHandle<'T>` — an opaque typed handle carrying the job id; the phantom `'T` gives the client compile-time safety. On the wire it is the single-case DU, `{"JobHandle":"<id>"}`.
- `JobStatus<'T>` — `Queued | Running of progress | Succeeded of 'T | Failed of message | Cancelled`.
- `IJobDispatcher` — `Enqueue<'T>: Async<'T> -> Async<JobHandle<'T>>`, `GetStatus<'T>: JobHandle<'T> -> Async<JobStatus<'T>>`, and (69i.G) `Cancel<'T>: JobHandle<'T> -> Async<unit>`. The shipped `InMemoryJobDispatcher` runs work via `Async.Start` under a per-job cancellation token and tracks status in-process.
- `Remoting.withJobDispatcher jobs` — composes the instance the handlers enqueue on. With it composed, every record field whose function shape finally returns `Async<JobHandle<'T>>` is classified long-running at startup (`LongRunning.classify`, the `Streaming.classify` pattern) and gains three routes beside its own:

| Route | Body | Reply | Task |
|---|---|---|---|
| `POST <route>/status` | `[handle]` | `JobStatus<'T>` — byte-identical to a hand-wired `JobHandle<'T> -> Async<JobStatus<'T>>` method (pinned) | 69i.C |
| `POST <route>/progress` | `[handle]` | Phase 69c SSE: one `event: chunk` per status **change**, the terminal status last, then `event: complete` | 69i.D |
| `POST <route>/cancel` | `[handle]` | as a `JobHandle<'T> -> Async<unit>` method replies (pinned) | 69i.G |

`<route>` is whatever the composed `RouteBuilder` produces for the start method (`/<Record>/<Method>` by default), so the companions are `/<Record>/<Method>/status` and so on. They are resolved from the last two path segments, which an ordinary one-segment method route never matches.

Nothing is composed by default and methods that don't use the shape are untouched (GP 11 / GP 13).

## Diff to apply

```fsharp
// API record — the long-op returns a typed handle. No poll method: the
// dispatcher serves StartReport/status, StartReport/progress and
// StartReport/cancel once the dispatcher is composed.
type ReportsApi = {
    [<RequiresRole "Analyst">]
    StartReport: ReportSpec -> Async<JobHandle<ReportFile>>
}

// Implementation — the handler enqueues on the dispatcher instance:
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
}

// Composition — the SAME instance, so the companions resolve the handles
// StartReport hands out:
Remoting.createApi ()
|> Remoting.fromValue reportsApi
|> Remoting.withAuthContext resolveSubject
|> Remoting.withJobDispatcher jobs
|> Remoting.buildHttpHandler
```

The client starts the operation, holds the handle, and either polls `<route>/status` until a terminal status (`Succeeded` / `Failed` / `Cancelled`) arrives, or subscribes to `<route>/progress` and reads frames with the public `SseFrame.parse` decoder — no casts, no bespoke status enum.

## How the pre-flight seams compose

The **start method is an ordinary method**. It runs the whole pre-flight chain — authorization (69d), validation (69e), idempotency (69f), rate-limit (69g), audit (69h) — before its handler enqueues. That is what makes two of the phase's guarantees hold by construction:

- **Authorization runs before enqueue (69i.E).** A denied caller never reaches the handler, so no job is created. The resolved subject (`IAuthContext.SubjectId`; `None` for a `[<PublicEndpoint>]` method, an anonymous caller, or no resolver) rides the call context for the request — `CallContext.subjectId ()` — and `InMemoryJobDispatcher.Enqueue` stamps the job with it, then **re-establishes it inside the job body** so sub-operations that key on the caller see the submitter. `GetStatus` / `Cancel` from any other subject answer as if the job did not exist (`Failed "job-not-found"` / silent no-op — no disclosure), unless the caller holds the job-admin role (below). A job enqueued with no subject resolved has no owner and is readable by anyone — the pre-69i posture, kept for deployments without authentication.
- **An idempotency key maps to one job (69i.F).** `[<Idempotent>]` on the start method plus a composed `IIdempotencyStore`: the second submit with the same `X-Idempotency-Key` replays the memoised response — the same `JobHandle` — and the handler is not invoked, so no second job exists. No key-to-job-id table is needed; the memoised reply *is* the mapping.

The **companions run outside the pre-flight chain**, like the 69c streaming path: they are served by the dispatcher off the composed instance, and their guard is ownership. Their caller is resolved through the composed `AuthContextResolver` every time (a companion is never public), and a caller for whom `IAuthContext.HasRole RemotingOptions.JobAdminRole` holds (default `"Admin"`; `Remoting.withJobAdminRole` overrides) may read and cancel any job. Rate-limit and audit attributes on the start method do not extend to its companions; a deployment that needs a companion audited wraps the route.

**Cancellation (69i.G)** trips the job's token (cooperative — every `Async` bind observes it) and marks `Cancelled` first-writer-wins against completion: a job that finishes as it is cancelled reports whichever landed first, never a later overwrite. An open `<route>/progress` subscriber receives `Cancelled` as its terminal frame and the stream closes.

## What the dispatcher does NOT route into: `IJobScheduler`

The phase's original text has the dispatcher enqueueing the handler body on Phase 9b's `IJobScheduler`. That is a **composition choice behind the `IJobDispatcher` seam**, not something the dispatcher can do for you: the shape the handler enqueues is a closure, and `JobResult` carries no payload, so a scheduler-backed dispatcher needs a result store beside the scheduler — exactly the shape Phases 630/631 built for the aggregate peer surface (`IPeerJobResultStore` over `IBlobStorage`, `Trigger.Manual` + `TriggerOnce`). That is a store-backed **implementation of `IJobDispatcher`**, and it is where "status persists across restart" lives; it is recorded as a decision on the forge Tidy-Up rather than half-built here.

**`InMemoryJobDispatcher` limits (dev / single-instance):** restarts wipe job state; live (`Queued` / `Running`) jobs are never evicted, and when the tracked-job cap (default 100 000) is reached with every slot live, `Enqueue` fails loudly with a saturation error rather than silently dropping jobs. The progress companion polls it every 100 ms (`LongRunning.PollInterval`) and frames only changes; a dispatcher with its own change feed can push instead.

## Verification

1. `dotnet build` — clean.
2. Call the start method: a handle returns immediately (sub-second), before the work completes.
3. `POST <route>/status` with `[handle]`: observe `Queued`/`Running`, then `Succeeded` carrying the typed result.
4. `POST <route>/progress` with `[handle]`: `event: chunk` frames, the last one terminal, then `event: complete`.
5. A handler that throws surfaces as `Failed` with the exception message — the poll never hangs.
6. Polling an unknown handle (or another subject's) returns `Failed "job-not-found"`, not an exception and not a disclosure.
7. `POST <route>/cancel` as the submitter: status transitions to `Cancelled`; as anyone else without the admin role: a no-op.

The contract pack is `src/ToolUp.Platform.Tests/InProcess/JobHandleTests.fs` (Phase 69i.H), which also pins the companion bodies byte-for-byte against hand-wired methods.

## Rollback

Additive — drop `Remoting.withJobDispatcher` and the companions disappear; the v0 hand-wired poll method keeps working. Revert the method signatures to their previous synchronous shape and drop the dispatcher instance to leave the shape entirely. Methods not using the shape were never affected. `IJobDispatcher` gained `Cancel`; an out-of-tree implementation adds the one member.

## See also

- [69-family-overview.md](69-family-overview.md) — family map and adoption sequence.
- [69c-streaming-asyncseq-adoption.md](69c-streaming-asyncseq-adoption.md) — the SSE framing the progress companion rides, and `SseFrame.parse`.
- [69f-idempotency-keys.md](69f-idempotency-keys.md) — the replay that makes one key one job.
- [69d-authorization-metadata.md](69d-authorization-metadata.md) — the resolver whose subject becomes the job's owner.
- Substrate: `src/ToolUp.Platform.Server/Server/Remoting/Jobs.fs`, `LongRunning.fs`, `CallContext.fs`.
