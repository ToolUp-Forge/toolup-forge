# 873 — The infrastructure seams report failure as data, and are asynchronous at every member

## What changes

Five seams that a distributed implementation could plausibly provide now satisfy portability rules 2 and 3: every
member is asynchronous, and a write that did not happen says so in its return type. A failed write can no longer
be invisible by construction, and a second implementation no longer has to invent an exception to report one.

| Seam | Member | Before | After |
|---|---|---|---|
| `IEventStore` (Core) | `Write` | `ModuleEvent -> Async<unit>`, raising on a lost write | `ModuleEvent -> Async<Result<unit, EventStoreError>>` |
| `IJobStore` (Server, Jobs part) | `Save`, `Update`, `RecordRun` | `Async<unit>`, raising | `Async<Result<unit, JobStoreError>>` |
| `IJobScheduler` (Server, Jobs part) | `Cancel`, `Disable`, `Enable` | `Async<unit>`, raising `InvalidOperationException` when busy | `Async<Result<unit, JobTransitionError>>` |
| `IWebhookDispatcher` (Server, Jobs part) | `Dispatch` | `ModuleEvent -> unit` | `ModuleEvent -> Async<Result<unit, WebhookDispatchError>>` |
| `ILiveSessionHost` (Server, Jobs part) | `Subscribe` | `handler: string -> unit` | `handler: string -> Async<unit>` |

`IJobScheduler.TriggerOnce` already answered `Async<Result<unit, TriggerError>>`, `JobDisabled` included, since
Phase 1066; it is unchanged. `NotifyEventWritten` stays `Async<unit>`: it is a dispatch request, not a write, and
an event-triggered job's own failures are recorded on its run rows. `IAuditLog.Record` moves in Phase 1062, which
builds on `IEventStore.Write` below.

### The new error types

```fsharp
// ToolUp.Platform (Core)
[<RequireQualifiedAccess>]
type EventStoreError =
    | WriteFailed of scopeId: string * eventId: Guid * location: string * reason: string
exception EventStoreWriteFailed of EventStoreError
module EventStore = val writeOrRaise: IEventStore -> ModuleEvent -> Async<unit>

// ToolUp.Platform.Jobs
[<RequireQualifiedAccess>]
type JobStoreError =
    | WriteFailed of scopeId: string * jobId: JobId * record: string * reason: string
    | PriorUnreadable of scopeId: string * jobId: JobId * reason: string
exception JobStoreWriteFailed of JobStoreError
module JobStoreWrite = val orRaise: Async<Result<unit, JobStoreError>> -> Async<unit>

[<RequireQualifiedAccess>]
type JobTransitionError =
    | Busy of scopeId: string * jobId: JobId * waited: TimeSpan
    | StoreFailed of JobStoreError
exception JobTransitionFailed of JobTransitionError
module JobTransition = val orRaise: Async<Result<unit, JobTransitionError>> -> Async<unit>

// ToolUp.Platform.Jobs.WebhookDispatcher
[<RequireQualifiedAccess>]
type WebhookDispatchError =
    | Dropped of scopeId: string * eventId: Guid * eventType: string * queueCapacity: int
    | DeliveryLogWriteFailed of scopeId: string * subscriptionId: Guid * deliveryId: Guid * reason: string
```

Each error has a `describe` / `message` function that renders it as one sentence.

### Behaviour that moves with it

- **`PersistentEventStore.Write` answers `WriteFailed`** where Phase 863 made it raise the internal
  `EventStoreWriteException`; that exception is removed. A failed index write stays best-effort, as before.
- **`BlobJobStore`** answers `WriteFailed` for a definition or run blob it could not write (it raised a bare
  `failwith`), and `PriorUnreadable` for a definition whose prior state it could not read (it raised
  `DocumentUnavailableException`).
- **Every first-party decorator** (`HookedEventStore`, `JobNotifyEventStore`, `AuditReplicationHookedEventStore`,
  `ObservingEventStore`, `QuartzJobStore`) answers its inner store's `Error` unchanged and runs its hook only
  after an `Ok`.
- **The in-process scheduler's busy refusal is `JobTransitionError.Busy`**, not the internal
  `JobStatusBusyException` (removed); the job admin API renders it, and a `StoreFailed`, with
  `JobTransitionError.message`, as before.
- **`Dispatch` on a full queue** writes the `WebhookEventDropped` audit row before it answers `Dropped` (it was a
  detached `Async.Start`). `HookedEventStore` answers `Ok` for a persisted event whatever the fan-out answered:
  the drop is the dispatcher's to report, not a failed write.
- **A delivery-log row that was not written** is `DeliveryLogWriteFailed` (Phase 1064's `recordDeliveryBestEffort`
  is renamed `recordDelivery` and answers it). The delivery stands; each delivery path carries its errors to the
  end of the fan-out, where they are logged with the `best-effort-write` marker and counted on
  `DeliveryLogWriteFailures`.
- **`InMemoryLiveSessionHost` awaits every subscriber's handler** before `PushFrame` completes, running them
  concurrently, and a failed handler is contained to itself. The SSE endpoint keeps Phase 870's single-writer
  queue: two concurrent pushes, and the keepalive, are still separate writers on one response.
- In-tree callers that can decide on a failure now do: `ReportSubscriptionApiHandler` refuses a delete whose job
  did not cancel, the tenant-offboard cancel answers the refusal, the job-scheduler lifecycle hook fails (so the
  resumable sweep re-runs it), and the scheduled-deprovision poll job retries as transient.

## The diff, per interface

### `IEventStore`

```diff
 type IEventStore =
-    abstract Write: ModuleEvent -> Async<unit>
+    abstract Write: ModuleEvent -> Async<Result<unit, EventStoreError>>
```

An implementation:

```diff
-        member _.Write(evt) = async { written.Add evt }
+        member _.Write(evt) = async {
+            written.Add evt
+            return Ok()
+        }
```

A decorator:

```diff
         member _.Write(evt) = async {
-            do! inner.Write evt
-            hook evt
+            match! inner.Write evt with
+            | Error _ as failed -> return failed
+            | Ok() ->
+                hook evt
+                return Ok()
         }
```

A caller that decides, and one whose failure path is already an exception:

```diff
-            do! eventStore.Write evt
+            match! eventStore.Write evt with
+            | Ok() -> ()
+            | Error error -> logger.Warn(EventStoreError.describe error)

-            do! eventStore.Write evt
+            do! EventStore.writeOrRaise eventStore evt
```

### `IJobStore`

```diff
-    abstract Save: definition: JobDefinition -> Async<unit>
-    abstract Update: definition: JobDefinition -> Async<unit>
-    abstract RecordRun: run: JobRun -> Async<unit>
+    abstract Save: definition: JobDefinition -> Async<Result<unit, JobStoreError>>
+    abstract Update: definition: JobDefinition -> Async<Result<unit, JobStoreError>>
+    abstract RecordRun: run: JobRun -> Async<Result<unit, JobStoreError>>
```

```diff
-        do! store.Update job
+        do! JobStoreWrite.orRaise <| store.Update job
```

`SchedulerPolicy.setStatus` answers `Async<Result<unit, JobStoreError>>`.

### `IJobScheduler`

```diff
-    abstract Cancel: scopeId: string * jobId: JobId -> Async<unit>
-    abstract Disable: scopeId: string * jobId: JobId -> Async<unit>
-    abstract Enable: scopeId: string * jobId: JobId -> Async<unit>
+    abstract Cancel: scopeId: string * jobId: JobId -> Async<Result<unit, JobTransitionError>>
+    abstract Disable: scopeId: string * jobId: JobId -> Async<Result<unit, JobTransitionError>>
+    abstract Enable: scopeId: string * jobId: JobId -> Async<Result<unit, JobTransitionError>>
```

A scheduler built on `SchedulerPolicy`:

```diff
-        member _.Cancel(scopeId, jobId) = SchedulerPolicy.setStatus store scopeId jobId Cancelled
+        member _.Cancel(scopeId, jobId) = async {
+            let! written = SchedulerPolicy.setStatus store scopeId jobId Cancelled
+            return Result.mapError JobTransitionError.StoreFailed written
+        }
```

A caller:

```diff
-        do! scheduler.Cancel(scopeId, jobId)
+        match! scheduler.Cancel(scopeId, jobId) with
+        | Ok() -> ()
+        | Error refusal -> return Error(JobTransitionError.message refusal)
```

`TenantLifecycleAggregator.cancelScheduledDeprovision` answers
`Async<Result<ScheduledDeprovision option, JobTransitionError>>`.

### `IWebhookDispatcher`

```diff
-    abstract Dispatch: event: ModuleEvent -> unit
+    abstract Dispatch: event: ModuleEvent -> Async<Result<unit, WebhookDispatchError>>
```

```diff
-        member _.Dispatch(_) = ()
+        member _.Dispatch(_) = async { return Ok() }
```

### `ILiveSessionHost`

```diff
-    abstract Subscribe: scopeId: string * sessionId: string * handler: (string -> unit) -> Async<Guid option>
+    abstract Subscribe: scopeId: string * sessionId: string * handler: (string -> Async<unit>) -> Async<Guid option>
```

```diff
-        host.Subscribe(scopeId, sessionId, received.Add)
+        host.Subscribe(scopeId, sessionId, fun frame -> async { received.Add frame })
```

## The contract packs

Each pack gains a failing-backend arm, so an external implementation is held to the same shape:

- `IEventStoreContract.failingBackendTests name factory` — a store over a backend that refuses every write answers
  `WriteFailed` naming the scope and the event, never `Ok` and never an exception, and a read returns nothing.
  `PersistentEventStore` binds it over `WriteRefusingBlobStorage` (`Contracts/InMemoryBlobStorage.fs`).
- `IEventStoreDecoratorContract` law 9 — a decorator answers its inner store's `Error` unchanged.
- `IJobStoreContract.failingBackendTests name factory` — `Save`, `Update` and `RecordRun` answer a `JobStoreError`,
  and nothing is read back. `BlobJobStore` and `QuartzJobStore` bind it.
- `IJobSchedulerContract.failingStoreTests name bind` — over a store that refuses (`RefusingJobStore`, which the
  pack supplies), `Disable` and `Cancel` answer `StoreFailed` and the job is unchanged. Both schedulers bind it.
- `ILiveSessionHostContract` — a push awaits its subscribers, and a failing subscriber is contained.

A store with no backend to fail (`InMemoryEventStore`) has no honest binding for the failing-backend arm and does
not bind it.

## Codemod

`dotnet run --project Build.fsproj -- Codemod --phase 873` (rules `873-*`):

- rewrites `do! <name>EventStore.Write evt` to `do! EventStore.writeOrRaise <name>EventStore evt`, and
  `do! <name>JobStore.Save|Update|RecordRun …` to `do! JobStoreWrite.orRaise <| <name>JobStore.…` — a receiver
  named for its seam is the only proof the codemod has of the type;
- reports every other event-store and job-store write, every `Cancel` / `Disable` / `Enable` call, every
  implementer of the five seams, and every scheduler implementer, because what a site does with a failure is
  the site's decision.

## Verification

`dotnet build ToolUp.Forge.sln` with zero errors and no `FS0020` over the new result types; the five packs above
green against every in-tree implementation.

## Rollback

Revert the phase's commits. No persisted byte changes: the event, job and run blobs are written exactly as
before.
